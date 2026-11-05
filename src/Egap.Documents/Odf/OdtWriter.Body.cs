using System.Globalization;
using System.Xml.Linq;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Documents.Odf;

/// <summary>
/// The body (ODF 1.3 Part 3, 3.4 and 5 to 8): sections as master pages, paragraphs and headings, lists, tables, text
/// sections for columns, and tables of contents; page layouts and master pages for the sections' page setup, headers and
/// footers.
/// </summary>
internal sealed partial class OdtWriter
{
    private readonly List<XElement> masterPages = [];
    private readonly List<string> sectionMasters = [];
    private readonly Dictionary<List, string> listElementIds = [];
    private readonly HashSet<ListDefinition> definitionsUsed = [];
    private readonly HashSet<(List, int)> levelsStarted = [];
    private int listElementCount;
    private int sectionCount;
    private int tocCount;

    /// <summary>The master page the next paragraph or table of the body starts, and the page number it restarts at.</summary>
    private (string Master, int? PageNumber)? pendingMaster;

    /// <summary>Where blocks go: the element, and the lists open in it (outermost first).</summary>
    private sealed class BlockTarget(XElement container, bool main)
    {
        public XElement Container { get; } = container;

        public bool Main { get; } = main;

        public List<XElement> Lists { get; } = [];

        public List? List { get; set; }
    }

    // ---- Master pages ----

    private void WriteMasterPages()
    {
        scope = masterStyles;
        Dictionary<HeaderFooterKind, HeaderFooter> current = [];
        Dictionary<HeaderFooter, int> ids = [];
        string? previousKey = null;
        int count = 0;
        allowChanges = false;
        for (int i = 0; i < document.Sections.Count; i++)
        {
            Section section = document.Sections[i];
            if (i > 0 && section.PageSetup.SectionStart == SectionStart.Continuous)
            {
                // A section that does not start a page has the page of the section before it; its columns are a text section's.
                sectionMasters.Add(sectionMasters[^1]);
                continue;
            }

            // Word's sections take the headers and footers of the section before them unless they have their own.
            foreach (HeaderFooter story in section.HeadersFooters)
            {
                current[story.Kind] = story;
            }

            bool first = section.PageSetup.DifferentFirstPage == true;
            string key = string.Join(',', current.OrderBy(c => c.Key).Select(c => c.Key + ":" + Id(c.Value))) + "|" + first + "|" + PageKey(section.PageSetup);
            if (key == previousKey)
            {
                sectionMasters.Add(sectionMasters[^1]);
                continue;
            }

            previousKey = key;
            string name = count++ == 0 ? "Standard" : "Section" + Int(count);
            sectionMasters.Add(WriteMaster(name, section.PageSetup, current, first));
        }

        if (document.Sections.Count > 0 && document.Sections.Any(s => s.MarginBoxes.Count > 0))
        {
            warnings.Add(ConversionWarningKind.PageLayoutDropped, "OpenDocument has no margin boxes; they were left out.");
        }

        allowChanges = true;
        scope = contentStyles;

        int Id(HeaderFooter story)
        {
            if (!ids.TryGetValue(story, out int id))
            {
                id = ids.Count;
                ids[story] = id;
            }

            return id;
        }
    }

    private static string PageKey(PageSetup p)
    {
        return string.Join(',', p.PageWidth, p.PageHeight, p.Orientation, p.MarginTop, p.MarginBottom, p.MarginLeft, p.MarginRight, p.HeaderDistance,
            p.FooterDistance, p.Gutter, p.PageNumberStyle, p.ColumnCount, p.ColumnSpacing, p.ColumnSeparator, p.Bidi,
            p.Columns is null ? "" : string.Join(';', p.Columns));
    }

    /// <summary>Writes a master page (and for ODF 1.2 a first-page master before it), and returns the master the section starts with.</summary>
    private string WriteMaster(string name, PageSetup setup, Dictionary<HeaderFooterKind, HeaderFooter> stories, bool firstPage)
    {
        HeaderFooter? header = stories.GetValueOrDefault(HeaderFooterKind.HeaderPrimary);
        HeaderFooter? footer = stories.GetValueOrDefault(HeaderFooterKind.FooterPrimary);
        string layout = PageLayout(setup, header is not null, footer is not null);
        XElement master = new(StyleNs + "master-page", new XAttribute(StyleNs + "name", name), new XAttribute(StyleNs + "page-layout-name", layout));
        bool even = document.Settings.EvenAndOddHeaders;
        bool firstHere = firstPage && Odf13;
        AddHeaderFooter(master, "header", header, even ? stories.GetValueOrDefault(HeaderFooterKind.HeaderEven) : null, firstHere ? stories.GetValueOrDefault(HeaderFooterKind.HeaderFirst) : null, firstHere);
        AddHeaderFooter(master, "footer", footer, even ? stories.GetValueOrDefault(HeaderFooterKind.FooterEven) : null, firstHere ? stories.GetValueOrDefault(HeaderFooterKind.FooterFirst) : null, firstHere);
        if (!firstPage || firstHere)
        {
            masterPages.Add(master);
            return name;
        }

        // ODF 1.2 has no first-page headers: a master page of its own for the first page, followed by the section's.
        HeaderFooter? firstHeader = stories.GetValueOrDefault(HeaderFooterKind.HeaderFirst);
        HeaderFooter? firstFooter = stories.GetValueOrDefault(HeaderFooterKind.FooterFirst);
        string firstName = "First_20_" + name;
        XElement first = new(StyleNs + "master-page", new XAttribute(StyleNs + "name", firstName),
            new XAttribute(StyleNs + "display-name", "First " + name), new XAttribute(StyleNs + "page-layout-name", PageLayout(setup, firstHeader is not null, firstFooter is not null)),
            new XAttribute(StyleNs + "next-style-name", name));
        AddHeaderFooter(first, "header", firstHeader, null, null, false);
        AddHeaderFooter(first, "footer", firstFooter, null, null, false);
        masterPages.Add(first);
        masterPages.Add(master);
        return firstName;
    }

    private void AddHeaderFooter(XElement master, string what, HeaderFooter? main, HeaderFooter? even, HeaderFooter? first, bool firstPage)
    {
        if (main is null && even is null && first is null)
        {
            return;
        }

        XElement element = new(StyleNs + what);
        if (main is null)
        {
            element.Add(new XAttribute(StyleNs + "display", "false"));
        }

        WriteStory(main?.Blocks, element);
        master.Add(element);
        if (document.Settings.EvenAndOddHeaders && main is not null)
        {
            XElement left = new(StyleNs + what + "-left");
            WriteStory(even?.Blocks, left);
            master.Add(left);
        }

        if (firstPage)
        {
            XElement firstElement = new(StyleNs + what + "-first");
            WriteStory(first?.Blocks, firstElement);
            master.Add(firstElement);
        }
    }

    /// <summary>
    /// A page layout (ODF 1.3 Part 3, 16.5): size, margins, columns and the header and footer areas. ODF's page margins hold
    /// the header and footer, Word's are where the body starts: the page margin is the header's distance, and the header's
    /// height reaches to Word's margin.
    /// </summary>
    private string PageLayout(PageSetup setup, bool header, bool footer)
    {
        double top = setup.MarginTop ?? 72;
        double bottom = setup.MarginBottom ?? 72;
        double headerDistance = Math.Clamp(setup.HeaderDistance ?? 36, 0, top);
        double footerDistance = Math.Clamp(setup.FooterDistance ?? 36, 0, bottom);
        XElement props = new(StyleNs + "page-layout-properties",
            new XAttribute(FoNs + "page-width", Pt(setup.PageWidth ?? 612)),
            new XAttribute(FoNs + "page-height", Pt(setup.PageHeight ?? 792)),
            new XAttribute(StyleNs + "print-orientation", setup.Orientation == PageOrientation.Landscape ? "landscape" : "portrait"),
            new XAttribute(FoNs + "margin-top", Pt(header ? headerDistance : top)),
            new XAttribute(FoNs + "margin-bottom", Pt(footer ? footerDistance : bottom)),
            new XAttribute(FoNs + "margin-left", Pt((setup.MarginLeft ?? 72) + (document.Settings.GutterAtTop ? 0 : setup.Gutter ?? 0))),
            new XAttribute(FoNs + "margin-right", Pt(setup.MarginRight ?? 72)));
        if (setup.PageNumberStyle is { } numbers)
        {
            props.Add(new XAttribute(StyleNs + "num-format", NumFormat(numbers) is { Length: > 0 } format ? format : "1"));
        }

        props.Add(new XAttribute(StyleNs + "writing-mode", setup.Bidi == true ? "rl-tb" : "lr-tb"));
        if (Columns(setup) is { } columns)
        {
            props.Add(columns);
        }

        XElement layout = new(StyleNs + "page-layout", new XAttribute(StyleNs + "name", ""), props);
        if (document.Settings.MirrorMargins)
        {
            layout.Add(new XAttribute(StyleNs + "page-usage", "mirrored"));
        }

        if (header)
        {
            layout.Add(new XElement(StyleNs + "header-style", HeaderFooterProperties(top - headerDistance, "margin-bottom")));
        }

        if (footer)
        {
            layout.Add(new XElement(StyleNs + "footer-style", HeaderFooterProperties(bottom - footerDistance, "margin-top")));
        }

        return scope.Add(layout, "page-layout");
    }

    private static XElement HeaderFooterProperties(double height, string spacing)
    {
        height = Math.Max(0, height);
        return new XElement(StyleNs + "header-footer-properties",
            new XAttribute(FoNs + "min-height", Pt(height)),
            new XAttribute(FoNs + spacing, Pt(Math.Min(height / 2, 14.4))),
            new XAttribute(FoNs + "margin-left", "0pt"),
            new XAttribute(FoNs + "margin-right", "0pt"),
            new XAttribute(StyleNs + "dynamic-spacing", "false"));
    }

    /// <summary><c>style:columns</c> (ODF 1.3 Part 3, 17.12) for more than one column, or null.</summary>
    private static XElement? Columns(PageSetup setup)
    {
        int count = setup.Columns is { Count: > 1 } list && setup.EqualColumnWidths == false ? list.Count : setup.ColumnCount ?? 1;
        if (count <= 1)
        {
            return null;
        }

        XElement columns = new(StyleNs + "columns", new XAttribute(FoNs + "column-count", Int(count)), new XAttribute(FoNs + "column-gap", Pt(setup.ColumnSpacing ?? 36)));
        if (setup.ColumnSeparator == true)
        {
            columns.Add(new XElement(StyleNs + "column-sep", new XAttribute(StyleNs + "width", "0.5pt"), new XAttribute(StyleNs + "color", "#000000"),
                new XAttribute(StyleNs + "height", "100%")));
        }

        if (setup.Columns is { Count: > 1 } widths && setup.EqualColumnWidths == false)
        {
            for (int i = 0; i < widths.Count; i++)
            {
                double start = i > 0 ? widths[i - 1].SpaceAfter / 2 : 0;
                double end = i < widths.Count - 1 ? widths[i].SpaceAfter / 2 : 0;
                columns.Add(new XElement(StyleNs + "column", new XAttribute(StyleNs + "rel-width", Int((long)Math.Round((widths[i].Width + start + end) * 20)) + "*"),
                    new XAttribute(FoNs + "start-indent", Pt(start)), new XAttribute(FoNs + "end-indent", Pt(end))));
            }
        }

        return columns;
    }

    // ---- Body ----

    private XElement WriteBody()
    {
        XElement text = new(OfficeNs + "text");
        XElement container = text;
        if (document.Protection.IsEnforced && document.Protection.Kind == ProtectionKind.Forms)
        {
            // As LibreOffice writes Word's form protection: the text in a protected section, only the form fields open.
            container = new XElement(TextNs + "section", new XAttribute(TextNs + "name", "TextSection"), new XAttribute(TextNs + "protected", "true"));
            text.Add(container);
        }

        story = new StoryState();
        for (int i = 0; i < document.Sections.Count; i++)
        {
            Section section = document.Sections[i];
            BlockTarget target = new(container, main: true);
            if (i == 0)
            {
                pendingMaster = sectionMasters[0] != "Standard" || section.PageSetup.PageNumberStart is not null
                    ? (sectionMasters[0], section.PageSetup.PageNumberStart)
                    : null;
            }
            else if (section.PageSetup.SectionStart == SectionStart.Continuous)
            {
                if (section.PageSetup.ColumnCount is > 1 || section.PageSetup.Columns is { Count: > 1 })
                {
                    XElement columns = new(TextNs + "section", new XAttribute(TextNs + "name", "Section" + Int(++sectionCount)));
                    XElement props = new(StyleNs + "section-properties", Columns(section.PageSetup));
                    if (section.PageSetup.BalanceColumns == false)
                    {
                        props.Add(new XAttribute(TextNs + "dont-balance-text-columns", "true"));
                    }

                    columns.Add(new XAttribute(TextNs + "style-name", Auto("section", null, null, props)));
                    container.Add(columns);
                    target = new BlockTarget(columns, main: true);
                }
            }
            else
            {
                pendingMaster = (sectionMasters[i], section.PageSetup.PageNumberStart);
            }

            WriteBlocks(section.Body.Blocks, target);
            if (pendingMaster is { } empty)
            {
                // A section with nothing in it still starts its page.
                target.Container.Add(new XElement(TextNs + "p", new XAttribute(TextNs + "style-name", ParagraphAuto(null, empty.Master, empty.PageNumber, null, null, false))));
                pendingMaster = null;
            }
        }

        CloseInsertion();
        if (document.Extensions.Get<OdtExtension>(OdtExtension.Format)?.Declarations is { } declarations && options.KeepUnknownMarkup)
        {
            // Forms come first in office:text, then tracked changes, then the declarations (ODF 1.3 Part 3, 3.4).
            List<XElement> kept = [.. declarations.Select(Keep)];
            text.AddFirst(kept.Where(d => d.Name.Namespace != OfficeNs || d.Name.LocalName != "forms"));
            InsertChanges(text);
            text.AddFirst(kept.Where(d => d.Name.Namespace == OfficeNs && d.Name.LocalName == "forms"));
        }
        else
        {
            InsertChanges(text);
        }

        return text;
    }

    private void InsertChanges(XElement text)
    {
        if (changedRegions.Count > 0 || document.Settings.TrackRevisions)
        {
            XElement tracked = new(TextNs + "tracked-changes", new XAttribute(TextNs + "track-changes", document.Settings.TrackRevisions ? "true" : "false"), changedRegions);
            text.AddFirst(tracked);
        }
    }

    /// <summary>Writes a story of its own (a header, note, comment, cell or text box) into <paramref name="container"/>.</summary>
    private void WriteStory(IEnumerable<Block>? blocks, XElement container, bool inNote = false)
    {
        StoryState saved = story;
        (string, int?)? savedMaster = pendingMaster;
        story = new StoryState { InNote = inNote || saved.InNote };
        pendingMaster = null;
        try
        {
            BlockTarget target = new(container, main: false);
            if (blocks is not null)
            {
                WriteBlocks(blocks, target);
            }

            CloseInsertion();
            if (!container.Elements().Any(e => e.Name == TextNs + "p" || e.Name == TextNs + "h" || e.Name == TextNs + "list" || e.Name == TableNs + "table"
                || e.Name == TextNs + "section" || e.Name == TextNs + "table-of-content"))
            {
                container.Add(new XElement(TextNs + "p", new XAttribute(TextNs + "style-name", "Standard")));
            }
        }
        finally
        {
            story = saved;
            pendingMaster = savedMaster;
        }
    }

    private void WriteBlocks(IEnumerable<Block> blocks, BlockTarget target)
    {
        List<Block> flat = Flatten(blocks);
        for (int i = 0; i < flat.Count; i++)
        {
            Block block = flat[i];
            if (options.KeepUnknownMarkup && block.Extensions.Get<OdtExtension>(OdtExtension.Format)?.Before is { } before)
            {
                CloseLists(target);
                foreach (XElement element in before)
                {
                    target.Container.Add(Keep(element));
                }
            }

            switch (block)
            {
                case Paragraph paragraph:
                    if (TocEnd(paragraph, flat, i) is int end)
                    {
                        CloseLists(target);
                        WriteToc(paragraph, flat, i, end, target);
                        i = end;
                        break;
                    }

                    // A deleted paragraph mark joins the paragraph to the next one, with the break inside a deletion.
                    int last = i;
                    while (last + 1 < flat.Count && flat[last] is Paragraph { MarkRevision.Removes: true } && flat[last + 1] is Paragraph && allowChanges)
                    {
                        last++;
                    }

                    WriteParagraph([.. flat.Skip(i).Take(last - i + 1).Cast<Paragraph>()], target);
                    i = last;
                    break;
                case Table table:
                    CloseLists(target);
                    WriteTable(table, target);
                    break;
            }
        }

        CloseLists(target);
    }

    /// <summary>The blocks as ODF holds them: paragraphs and tables, content controls giving their blocks and containers a table.</summary>
    private List<Block> Flatten(IEnumerable<Block> blocks)
    {
        List<Block> result = [];
        foreach (Block block in blocks)
        {
            Add(block);
        }

        return result;

        void Add(Block block)
        {
            switch (block)
            {
                case Paragraph or Table:
                    result.Add(block);
                    break;
                case FlexContainer or GridContainer:
                    warnings.Add(ConversionWarningKind.TableSimplified, "OpenDocument has no flex or grid containers; each became a borderless table.");
                    if (Docx.ContainerTables.For(block, Docx.ContainerTables.TextWidth(block)) is { } table)
                    {
                        result.Add(table);
                    }

                    break;
                case ContentControl control:
                    warnings.Add(ConversionWarningKind.ContentControlsDropped, "OpenDocument has no content controls; their content was kept.");
                    if (control.Box is { } box && (box.Draws || box.Horizontal > 0 || box.Vertical > 0 || box.Width.Unit != WidthUnit.Auto)
                        && Docx.ContainerTables.For(control, Docx.ContainerTables.TextWidth(control)) is { } boxed)
                    {
                        result.Add(boxed);
                    }
                    else
                    {
                        foreach (Block child in control.Blocks)
                        {
                            Add(child);
                        }
                    }

                    break;
                default:
                    warnings.Add(ConversionWarningKind.DrawingDropped, "A block kept from another format has no place in OpenDocument and was left out.");
                    break;
            }
        }
    }

    // ---- Paragraphs ----

    /// <summary>
    /// Writes a paragraph, or paragraphs joined by deleted paragraph marks, as one <c>text:p</c> or <c>text:h</c>, in its
    /// list when it has one. Page and column breaks inside it split it, as ODF breaks only between paragraphs.
    /// </summary>
    private void WriteParagraph(List<Paragraph> group, BlockTarget target)
    {
        Paragraph paragraph = group[0];
        (List? list, int level) = ListOf(paragraph);
        int? outline = OutlineOf(paragraph);
        bool heading = outline is not null;
        XElement container;
        if (list is not null && !(heading && list == outlineList))
        {
            container = ListItem(target, list, level);
        }
        else
        {
            CloseLists(target);
            container = target.Container;
        }

        (string Master, int? PageNumber)? master = target.Main ? pendingMaster : null;
        if (master is not null)
        {
            pendingMaster = null;
        }

        bool empty = !group.Any(p => p.Inlines.Any(i => i is not (BookmarkStart or BookmarkEnd)));

        // A page or column break that ends the paragraph is a break after it.
        Break? trailing = group[^1].Inlines.LastOrDefault(i => i is not (BookmarkStart or BookmarkEnd or CommentRangeStart or CommentRangeEnd
            or EditableRangeStart or EditableRangeEnd)) as Break;
        if (trailing is not { Kind: not BreakKind.Line, Revision: null })
        {
            trailing = null;
        }

        (string? Before, bool WithMaster) made = (null, true);
        XElement Make(string? breakBefore, bool withMaster)
        {
            made = (breakBefore, withMaster);
            XElement element = new(TextNs + (heading ? "h" : "p"));
            string style = ParagraphAuto(paragraph, withMaster ? master?.Master : null, withMaster ? master?.PageNumber : null, breakBefore, null, empty);
            element.Add(new XAttribute(TextNs + "style-name", style));
            if (heading)
            {
                element.Add(new XAttribute(TextNs + "outline-level", Int(outline!.Value + 1)));
                if (paragraph.Format.List == List.None && outlineList is not null)
                {
                    element.Add(new XAttribute(TextNs + "is-list-header", "true"));
                }
            }

            container.Add(element);
            return element;
        }

        ParagraphWriter writer = new(this, Make(null, withMaster: true), Make) { Skip = trailing };
        for (int g = 0; g < group.Count; g++)
        {
            writer.Paragraph = group[g];
            WriteInlines(group[g], writer);
            if (g + 1 < group.Count)
            {
                // The deleted paragraph mark: two paragraphs in the deletion are the break between them (ODF 1.3 Part 3, 5.5.4).
                writer.DeleteMark(group[g].MarkRevision!);
            }
        }

        if (trailing is not null)
        {
            writer.Element.SetAttributeValue(TextNs + "style-name", ParagraphAuto(paragraph, made.WithMaster ? master?.Master : null,
                made.WithMaster ? master?.PageNumber : null, made.Before, trailing.Kind == BreakKind.Page ? "page" : "column", empty));
        }

        Paragraph lastParagraph = group[^1];
        writer.EndParagraph(lastParagraph.MarkRevision);
    }

    /// <summary>The list a paragraph is in, by its own formatting or its style's, and its level; null for none.</summary>
    private (List? List, int Level) ListOf(Paragraph paragraph)
    {
        List? list = paragraph.Format.List;
        int? level = paragraph.Format.ListLevel;
        for (Style? s = paragraph.Style; s is not null && (list is null || level is null); s = s.BasedOn)
        {
            if (s is ParagraphStyle p)
            {
                list ??= p.ParagraphFormat.List;
                level ??= p.ParagraphFormat.ListLevel;
            }
        }

        return list is null || list == List.None || !document.Lists.Contains(list) ? (null, 0) : (list, Math.Clamp(level ?? 0, 0, 8));
    }

    private static int? OutlineOf(Paragraph paragraph)
    {
        int? level = paragraph.Format.OutlineLevel;
        for (Style? s = paragraph.Style; s is not null && level is null; s = s.BasedOn)
        {
            level = (s as ParagraphStyle)?.ParagraphFormat.OutlineLevel;
        }

        return level is >= 0 and <= 8 ? level : null;
    }

    /// <summary>
    /// The paragraph's automatic style: its own formatting over its common style, with a master page to start, a page number
    /// to restart at, a break before it, and the paragraph mark's formatting for an empty paragraph.
    /// </summary>
    private string ParagraphAuto(Paragraph? paragraph, string? master, int? pageNumber, string? breakBefore, string? breakAfter, bool markFormat)
    {
        string parent = ParagraphStyleName(paragraph?.Style);
        ParagraphFormat format = paragraph?.Format ?? new ParagraphFormat();
        if (paragraph is not null && format.TabStops is not null)
        {
            // A paragraph's tab stops add to its style's in the model, and replace them in ODF.
            ParagraphFormat merged = new();
            merged.CopyRaw(format);
            merged.TabStops = paragraph.GetEffectiveFormat().TabStops;
            format = merged;
        }

        XElement? props = ParagraphProperties(format, paragraph is null ? 0 : IndentOf(paragraph.Style, paragraph.Format));
        if (pageNumber is not null || breakBefore is not null || breakAfter is not null)
        {
            props ??= new XElement(StyleNs + "paragraph-properties");
            if (pageNumber is { } number)
            {
                props.SetAttributeValue(StyleNs + "page-number", Int(Math.Max(0, number)));
            }

            if (breakBefore is not null)
            {
                props.SetAttributeValue(FoNs + "break-before", breakBefore);
            }

            if (breakAfter is not null)
            {
                props.SetAttributeValue(FoNs + "break-after", breakAfter);
            }
        }

        XElement? text = markFormat && paragraph is not null && !paragraph.MarkFormat.IsEmpty ? TextProperties(paragraph.MarkFormat, null) : null;
        if (props is null && text is null && master is null)
        {
            return parent;
        }

        return Auto("paragraph", parent, master, props, text);
    }

    // ---- Lists ----

    /// <summary>A new list item for a paragraph at <paramref name="level"/> of <paramref name="list"/>, opening the lists it needs.</summary>
    private XElement ListItem(BlockTarget target, List list, int level)
    {
        if (target.List != list)
        {
            CloseLists(target);
            string id = "list" + Int(++listElementCount);
            XElement element = new(TextNs + "list", new XAttribute(XmlNs + "id", id), new XAttribute(TextNs + "style-name", ListStyleName(list.Definition)));
            if (listElementIds.TryGetValue(list, out string? previous))
            {
                element.Add(new XAttribute(TextNs + "continue-list", previous));
            }
            else if (definitionsUsed.Contains(list.Definition) && !Restarts(list))
            {
                element.Add(new XAttribute(TextNs + "continue-numbering", "true"));
            }

            definitionsUsed.Add(list.Definition);
            listElementIds[list] = id;
            target.Container.Add(element);
            target.Lists.Add(element);
            target.List = list;
        }

        while (target.Lists.Count > level + 1)
        {
            target.Lists.RemoveAt(target.Lists.Count - 1);
        }

        while (target.Lists.Count < level + 1)
        {
            XElement holder = new(TextNs + "list-item");
            target.Lists[^1].Add(holder);
            XElement nested = new(TextNs + "list");
            holder.Add(nested);
            target.Lists.Add(nested);
        }

        XElement item = new(TextNs + "list-item");
        if (levelsStarted.Add((list, level)) && list.Overrides[level].StartAt is { } start && start != list.Definition.Levels[level].StartAt)
        {
            item.Add(new XAttribute(TextNs + "start-value", Int(Math.Max(0, start))));
        }

        target.Lists[^1].Add(item);
        return item;
    }

    private static bool Restarts(List list)
    {
        return list.Overrides.Any(o => o.StartAt is not null);
    }

    private static void CloseLists(BlockTarget target)
    {
        target.Lists.Clear();
        target.List = null;
    }

    // ---- Tables of contents ----

    /// <summary>The index of the paragraph a table of contents that starts this paragraph ends in, or null.</summary>
    private static int? TocEnd(Paragraph paragraph, List<Block> flat, int index)
    {
        if (paragraph.Inlines.FirstOrDefault(i => i is not (BookmarkStart or BookmarkEnd)) is not FieldStart start
            || start.GetField() is not { } field || !IsToc(field.Code))
        {
            return null;
        }

        for (int i = index; i < flat.Count; i++)
        {
            if (flat[i] is not Paragraph p)
            {
                return null;
            }

            if (field.End.Paragraph == p)
            {
                return i;
            }
        }

        return null;
    }

    private static bool IsToc(string code)
    {
        ReadOnlySpan<char> trimmed = code.AsSpan().TrimStart();
        return trimmed.StartsWith("TOC", StringComparison.OrdinalIgnoreCase) && (trimmed.Length == 3 || char.IsWhiteSpace(trimmed[3]));
    }

    /// <summary>
    /// A table of contents (ODF 1.3 Part 3, 8.3): Word's TOC field and its result paragraphs as <c>text:table-of-content</c>
    /// with its entries as they stand, or <c>text:illustration-index</c> for a list of captions.
    /// </summary>
    private void WriteToc(Paragraph first, List<Block> flat, int start, int end, BlockTarget target)
    {
        FieldStart field = (FieldStart)first.Inlines.First(i => i is FieldStart);
        string code = field.GetField()?.Code ?? "TOC";
        FieldCode parsed = FieldCode.Parse(code);
        string? captions = parsed.Switch("\\C") is { Length: > 0 } c ? c : null;
        int levels = 3;
        if (parsed.Switch("\\O") is { } range && range.Split('-') is [_, var high] && int.TryParse(high, CultureInfo.InvariantCulture, out int top))
        {
            levels = Math.Clamp(top, 1, 10);
        }

        string kind = captions is null ? "table-of-content" : "illustration-index";
        XElement index = new(TextNs + kind, new XAttribute(TextNs + "name", (captions is null ? "Table of Contents" : "Illustration Index") + Int(++tocCount)),
            new XAttribute(TextNs + "protected", "false"));
        XElement source = new(TextNs + kind + "-source");
        if (captions is null)
        {
            source.Add(new XAttribute(TextNs + "outline-level", Int(levels)));
            for (int level = 1; level <= levels; level++)
            {
                string? entryStyle = document.Styles.FirstOrDefault(s => s.Id.Equals("TOC" + Int(level), StringComparison.OrdinalIgnoreCase)) is { } toc ? StyleName(toc) : null;
                source.Add(new XElement(TextNs + "table-of-content-entry-template", new XAttribute(TextNs + "outline-level", Int(level)),
                    new XAttribute(TextNs + "style-name", entryStyle ?? "Standard"),
                    new XElement(TextNs + "index-entry-link-start"), new XElement(TextNs + "index-entry-chapter"), new XElement(TextNs + "index-entry-text"),
                    new XElement(TextNs + "index-entry-tab-stop", new XAttribute(StyleNs + "type", "right"), new XAttribute(StyleNs + "leader-char", ".")),
                    new XElement(TextNs + "index-entry-page-number"), new XElement(TextNs + "index-entry-link-end")));
            }
        }
        else
        {
            source.Add(new XAttribute(TextNs + "caption-sequence-name", Clean(captions)), new XAttribute(TextNs + "caption-sequence-format", "text"),
                new XElement(TextNs + "illustration-index-entry-template", new XAttribute(TextNs + "style-name", "Standard"),
                    new XElement(TextNs + "index-entry-text"), new XElement(TextNs + "index-entry-tab-stop", new XAttribute(StyleNs + "type", "right"),
                    new XAttribute(StyleNs + "leader-char", ".")), new XElement(TextNs + "index-entry-page-number")));
        }

        XElement body = new(TextNs + "index-body");
        index.Add(source, body);
        target.Container.Add(index);
        BlockTarget entries = new(body, target.Main);
        story.TocField = field;
        for (int i = start; i <= end; i++)
        {
            WriteParagraph([(Paragraph)flat[i]], entries);
        }

        story.TocField = null;
    }
}
