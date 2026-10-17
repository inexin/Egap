using Egap.Documents;

namespace Egap.Pdf.Layout;

/// <summary>
/// Builds the flow model from analysed pages (Build Plan task 11, design Q6 to Q8): pages joined into sections by size,
/// margins and columns; paragraphs with styles made from repeated formatting; runs with mapped fonts; list items in lists;
/// tables with their grid, spans and header rows; figures as pictures; running headers and footers with PAGE fields; and
/// footnotes as notes at their markers.
/// </summary>
internal sealed partial class ModelBuilder
{
    private readonly PdfDocument pdf;
    private readonly PdfToDocumentOptions options;
    private readonly Document document = new();
    private readonly List<PageDraft> pages;
    private readonly Dictionary<PageGlyph, BlockDraft> notes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<BlockDraft> linked = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BlockDraft, PageDraft> pageOf = new(ReferenceEqualityComparer.Instance);
    private readonly RunWriter writer;
    private readonly ListBuilder lists;
    private StyleSheet styles = null!;
    private Paragraph? last;
    private Table? lastTable;
    private int noteCount;

    private ModelBuilder(PdfDocument pdf, List<PageDraft> pages, List<Roles.NoteLink> links, PdfToDocumentOptions options)
    {
        this.pdf = pdf;
        this.pages = pages;
        this.options = options;
        foreach (Roles.NoteLink link in links)
        {
            notes[link.Marker] = link.Note;
            linked.Add(link.Note);
        }

        foreach (PageDraft page in pages)
        {
            foreach (BlockDraft block in page.Blocks)
            {
                pageOf[block] = page;
            }
        }

        writer = new RunWriter(document, g => notes.GetValueOrDefault(g), WriteNote);
        lists = new ListBuilder(document);
    }

    public static Document Build(PdfDocument pdf, List<PageDraft> pages, List<Roles.NoteLink> links, PdfToDocumentOptions options, CancellationToken cancellationToken)
    {
        return new ModelBuilder(pdf, pages, links, options).Build(cancellationToken);
    }

    private Document Build(CancellationToken cancellationToken)
    {
        if (pdf.Metadata.Title is { Length: > 0 } title)
        {
            document.Properties.Title = Clean(title);
        }

        List<SectionPlan> plans = SectionPlanner.Plan(pages, Items);
        List<(BlockDraft, Frame)> framed = [.. plans.SelectMany(s => s.Items).Where(i => i.Item is BlockDraft).Select(i => ((BlockDraft)i.Item, i.Frame))];
        framed.AddRange(Furniture().Select(b => (b, Body(plans, b))));
        framed.AddRange(linked.Select(b => (b, Body(plans, b))));
        styles = new StyleSheet(document, framed);
        if (options.Mode == ReconstructionMode.Exact)
        {
            BuildExact(cancellationToken);
            return Finish();
        }

        Spacing(plans);
        FindScattered();

        Section? previous = null;
        foreach (SectionPlan plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Section section = document.AddSection();
            Setup(section, plan, previous);
            if (plan.NewPage)
            {
                Headers(section, plan, plans);
            }

            PageDraft? page = null;
            foreach (PlacedItem placed in plan.Items)
            {
                if (!ReferenceEquals(placed.Page, page))
                {
                    page = placed.Page;
                    writer.SetPage(page.Content);
                }

                switch (placed.Item)
                {
                    case BlockDraft block when Positioned(block, placed.Page, out WrapMode wrap):
                        WriteFrame(block, wrap);
                        break;
                    case BlockDraft block:
                        Write(section.Body, block, placed.Frame);
                        break;
                    case TableDraft table:
                        WriteTable(section.Body, table, placed.Frame);
                        break;
                    case FigureDraft figure:
                        WriteFigure(section.Body, figure, placed);
                        break;
                }
            }

            // Frames and floating figures still waiting for a paragraph get one of their own.
            if (floats.Count > 0)
            {
                Paragraph anchor = new(document) { Style = styles.Normal.Style };
                section.Body.Blocks.Add(anchor);
                TakeFloats(anchor);
            }

            previous = section;
        }

        return Finish();
    }

    private Document Finish()
    {
        foreach (Section section in document.Sections)
        {
            if (section.Body.Blocks.Count == 0 || section.Body.Blocks[^1] is not Paragraph)
            {
                section.Body.AddParagraph();
            }
        }

        if (options.EmbedFonts)
        {
            EmbedFonts();
        }

        return document;
    }

    /// <summary>A page's body in order: blocks, tables and figures by reading order, then the notes no marker in the text points to.</summary>
    private IReadOnlyList<object> Items(PageDraft page)
    {
        List<object> body = [.. page.Blocks.Where(b => !Furniture(b) && b.Role != LayoutRole.Footnote).Cast<object>().Concat(page.Tables).Concat(page.Figures)
            .OrderBy(Order)];
        body.AddRange(page.Blocks.Where(b => b.Role == LayoutRole.Footnote && !linked.Contains(b)).OrderBy(b => b.Box.Top));
        return body;
    }

    private IEnumerable<BlockDraft> Furniture()
    {
        return pages.SelectMany(p => p.Blocks).Where(Furniture);
    }

    private static bool Furniture(BlockDraft block)
    {
        return block.Role is LayoutRole.PageHeader or LayoutRole.PageFooter or LayoutRole.PageNumber;
    }

    private static int Order(object item)
    {
        return item switch
        {
            BlockDraft b => b.Order,
            TableDraft t => t.Order,
            FigureDraft f => f.Order,
            _ => 0,
        };
    }

    /// <summary>The text area of the page a block sits on, for blocks outside the body (headers, footers, notes).</summary>
    private Frame Body(List<SectionPlan> plans, BlockDraft block)
    {
        int index = pageOf.TryGetValue(block, out PageDraft? page) ? page.Content.Index : 0;
        SectionPlan? plan = plans.LastOrDefault(p => p.FirstPage.Content.Index <= index) ?? plans.FirstOrDefault();
        return plan is null ? new Frame(72, 540) : new Frame(plan.Left, plan.Width - plan.Right);
    }

    private static void Setup(Section section, SectionPlan plan, Section? previous)
    {
        PageSetup setup = section.PageSetup;
        setup.PageWidth = plan.Width;
        setup.PageHeight = plan.Height;
        setup.Orientation = plan.Width > plan.Height ? PageOrientation.Landscape : PageOrientation.Portrait;
        setup.MarginLeft = plan.Left;
        setup.MarginRight = plan.Right;
        setup.MarginTop = plan.Top;
        setup.MarginBottom = plan.Bottom;
        if (previous is not null)
        {
            setup.SectionStart = plan.NewPage ? SectionStart.NewPage : SectionStart.Continuous;
        }

        IReadOnlyList<Frame> columns = plan.Columns;
        setup.ColumnCount = columns.Count;
        if (columns.Count > 1)
        {
            List<double> gaps = [.. columns.Zip(columns.Skip(1)).Select(p => p.Second.Left - p.First.Right)];
            setup.ColumnSpacing = Math.Round(gaps.Average() * 2) / 2;
            bool equal = columns.All(c => Math.Abs(c.Width - columns[0].Width) <= columns[0].Width * 0.05);
            setup.EqualColumnWidths = equal;
            if (!equal)
            {
                setup.Columns = [.. columns.Select((c, i) => new TextColumn(Math.Round(c.Width * 2) / 2, i < gaps.Count ? Math.Round(gaps[i] * 2) / 2 : 0))];
            }
        }
    }

    /// <summary>Writes a block as a paragraph, or into the paragraph it carries on.</summary>
    private void Write(Body body, BlockDraft block, Frame frame)
    {
        StyleEntry entry = styles.For(block);
        if (block.Continues && last is not null && block.Role is LayoutRole.Text or LayoutRole.ListItem or LayoutRole.Quote)
        {
            writer.Write(last, block.Lines, styles.For(block).Font);
            return;
        }

        Paragraph paragraph = new(document) { Style = entry.Style };
        int skip = 0;
        bool list = false;
        if (block.Role == LayoutRole.ListItem && block.Marker is string text && ListBuilder.Parse(text, true) is Marker marker)
        {
            PageLine first = block.Lines[0];
            double textLeft = first.Words.Count > 1 ? first.Words[1].Box.Left : first.Box.Left;
            lists.Add(paragraph, marker, Math.Max(block.Level, 1) - 1, first.Box.Left - frame.Left, textLeft - frame.Left);
            skip = 1;
            list = true;
        }
        else if (block.Role is not (LayoutRole.Text or LayoutRole.Footnote))
        {
            lists.Break();
        }

        styles.ApplyDifferences(paragraph, block, list);
        if (spaceBefore.TryGetValue(block, out double before))
        {
            paragraph.Format.SpaceBefore = before;
        }

        if (block.Role == LayoutRole.Footnote)
        {
            skip = MarkerWords(block);
        }

        writer.Write(paragraph, block.Lines, entry.Font, skip, block.Role == LayoutRole.Code);
        body.Blocks.Add(paragraph);
        TakeFloats(paragraph);
        last = paragraph;
        lastTable = null;
    }

    /// <summary>A note's text in a footnote at its marker in the body text.</summary>
    private void WriteNote(Paragraph paragraph, BlockDraft note)
    {
        Footnote footnote = new(document);
        noteCount++;
        if (note.Marker is string marker && marker != noteCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            footnote.CustomMark = Clean(marker);
        }

        StyleEntry entry = styles.For(note);
        Paragraph text = new(document) { Style = entry.Style };
        writer.Write(text, note.Lines, entry.Font, MarkerWords(note));
        footnote.Story.Blocks.Add(text);
        paragraph.Inlines.Add(footnote);
    }

    /// <summary>How many of a note's first words are its marker: one when the first word is the marker with its brackets or stop.</summary>
    private static int MarkerWords(BlockDraft note)
    {
        PageLine first = note.Lines[0];
        string word = first.Words[0].Text.Trim('(', ')', '[', ']', '.', ':');
        return note.Marker is string marker && word == marker && first.Words.Count > 1 ? 1 : 0;
    }

    /// <summary>
    /// Copies the TrueType and OpenType programs of the fonts the text is set in into the document (design Q6, only when
    /// asked): one per family, bold and italic. Most PDF fonts are subsets, so the copy may lack glyphs an editor types later.
    /// </summary>
    private void EmbedFonts()
    {
        HashSet<(string, bool, bool)> done = [];
        foreach (PageGlyph glyph in pages.SelectMany(p => p.Content.Glyphs).Where(g => !g.IsArtifact && g.Font is { IsEmbedded: true }))
        {
            PdfFont font = glyph.Font!;
            (string, bool, bool) key = (FontNames.Map(FontTraits.Family(font)), FontTraits.IsBold(font), FontTraits.IsItalic(font));
            if (key.Item1.Length == 0 || done.Contains(key))
            {
                continue;
            }

            PdfDictionary? descriptor = font.Dictionary.Get<PdfDictionary>("FontDescriptor")
                ?? (font.Dictionary.Get<PdfArray>("DescendantFonts") is { Count: > 0 } descendants && descendants[0].Resolve() is PdfDictionary descendant
                    ? descendant.Get<PdfDictionary>("FontDescriptor") : null);
            PdfStream? program = descriptor?.Get<PdfStream>("FontFile2")
                ?? (descriptor?.Get<PdfStream>("FontFile3") is { } file3 && file3.Dictionary.Get<PdfName>("Subtype")?.Value == "OpenType" ? file3 : null);
            if (program is null)
            {
                continue;
            }

            try
            {
                document.EmbeddedFonts.Add(new EmbeddedFont(key.Item1, key.Item2, key.Item3, program.GetDecodedBytes()));
                done.Add(key);
            }
            catch (PdfException)
            {
                // A broken font program stays out.
            }
        }
    }

    /// <summary>Text without the control characters and lone surrogates that some PDF strings hold and no document can.</summary>
    internal static string Clean(string text)
    {
        return text.Any(c => char.IsControl(c) || char.IsSurrogate(c)) ? string.Concat(text.Where(c => !char.IsControl(c) && !char.IsSurrogate(c))) : text;
    }
}
