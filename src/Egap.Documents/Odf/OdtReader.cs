using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Documents.Odf;

/// <summary>
/// Reads OpenDocument Text (ODT, OTT and flat FODT; ODF 1.2 to 1.4) into the model. Styles are read first (see
/// OdtReader.Styles.cs); the body then builds paragraphs, lists, tables, frames and sections, and notes, comments, headers
/// and text boxes are read as stories of their own. Markup the model has no place for is kept as extension data
/// (<see cref="OdtExtension"/>).
/// </summary>
internal sealed partial class OdtReader
{
    /// <summary>The most entries the reader takes into any one table it builds (styles, lists, comments).</summary>
    private const int MaxEntries = 10_000;

    private readonly OdfPackage package;
    private readonly DocumentLimits limits;
    private readonly Document document = new(blank: true);
    private readonly Dictionary<(ConversionWarningKind Kind, string Message), int> warnings = [];
    private readonly HashSet<HeaderFooterKind> headerFootersSeen = [];
    private readonly Dictionary<XElement, List> lastListOfStyle = [];
    private readonly Dictionary<string, List> listsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Comment Comment, Paragraph Paragraph)> openComments = new(StringComparer.Ordinal);
    private readonly HashSet<string> referencedNotes = new(StringComparer.Ordinal);
    private readonly List<XElement> declarations = [];
    private int depth;
    private int styleCount;
    private int listCount;
    private Section section = null!;
    private string? currentMaster;
    private Pending pending = new();

    /// <summary>What waits for the next block or paragraph of a story: page-anchored frames, unknown markup, a page break.</summary>
    private sealed class Pending
    {
        public List<Inline> Inlines { get; } = [];

        public List<XElement>? Before { get; set; }

        public bool PageBreak { get; set; }
    }

    /// <summary>Where blocks go: a story's blocks, and whether this is the main story, whose master pages make sections.</summary>
    private sealed class BlockContext(NodeCollection<Block> blocks, bool main)
    {
        public NodeCollection<Block> Blocks { get; set; } = blocks;

        public bool Main { get; } = main;
    }

    /// <summary>A paragraph's white space state (ODF 1.3 Part 3, 6.1.2), shared by everything inside it.</summary>
    private sealed class WhiteSpace
    {
        public bool AtStart { get; set; } = true;

        public bool LastSpace { get; set; }
    }

    /// <summary>Where inlines go, and the text gathered for the next run.</summary>
    private sealed class InlineContext(Paragraph paragraph, NodeCollection<Inline> inlines, bool topLevel, BlockContext blocks, WhiteSpace space)
    {
        public Paragraph Paragraph { get; set; } = paragraph;

        public NodeCollection<Inline> Inlines { get; set; } = inlines;

        public bool TopLevel { get; } = topLevel;

        public BlockContext Blocks { get; } = blocks;

        public WhiteSpace Space { get; } = space;

        public StringBuilder Text { get; } = new();

        public RunFormat? TextFormat { get; set; }

        public CharacterStyle? TextStyle { get; set; }

        public Revision? TextRevision { get; set; }
    }

    /// <summary>The list a paragraph is in: the list, the level, the list style and whether its label is hidden.</summary>
    private sealed record ListState(List List, int Level, XElement? Style, bool Unnumbered);

    private OdtReader(OdfPackage package, OdtLoadOptions options)
    {
        this.package = package;
        limits = options.Limits;
    }

    public static Document Read(byte[] bytes, OdtLoadOptions options, out IReadOnlyList<ConversionWarning> warnings)
    {
        OdtReader reader;
        try
        {
            reader = new OdtReader(OdfPackage.Read(bytes, options.Limits, options.Password, options.PasswordCallback), options);
            reader.ReadDocument();
        }
        catch (Exception e) when (e is not (DocumentException or OutOfMemoryException))
        {
            // Malformed input must not escape as an arbitrary exception: callers catch DocumentException for a bad file.
            throw new DocumentException("The OpenDocument file is malformed: " + e.Message, e);
        }

        warnings = [.. reader.warnings.Select(w => new ConversionWarning(w.Key.Kind, w.Key.Message, w.Value))];
        return reader.document;
    }

    private void ReadDocument()
    {
        document.Settings.CompatibilityMode = 15;
        ReadSettings();
        ReadStyleTables();
        ReadMeta();
        XElement body = package.Content.Element(OfficeNs + "body")?.Element(OfficeNs + "text")
            ?? throw new DocumentException("The OpenDocument file has no text body (office:body/office:text).");
        ReadChanges(body);
        foreach (XElement reference in body.Descendants(TextNs + "note-ref"))
        {
            if (reference.Attr(TextNs, "ref-name") is { } name)
            {
                referencedNotes.Add(name);
            }
        }

        section = document.Sections.AddTrusted(new Section(document));
        string? first = FirstMaster(body) ?? (masterPages.ContainsKey("Standard") ? "Standard" : firstMasterPage);
        if (first is not null)
        {
            ApplyMaster(section, first);
            currentMaster = first;
        }

        BlockContext main = new(section.Body.Blocks, main: true);
        ReadBlocks(body, main, null);
        FinishStory(main);
        CloseComments();

        for (int i = document.Sections.Count - 1; i > 0; i--)
        {
            if (document.Sections[i].Body.Blocks.Count == 0)
            {
                document.Sections[i].Remove();
            }
        }

        if (declarations.Count > 0)
        {
            OdtExtension.Of(document).Declarations = declarations;
        }

        OdtExtension.Of(document).Package = package;
    }

    private void Enter()
    {
        if (++depth > limits.MaxDepth || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new DocumentLimitException($"The document nests deeper than the limit of {limits.MaxDepth}.");
        }
    }

    private void Count()
    {
        if (++styleCount > MaxEntries * 10)
        {
            throw new DocumentLimitException($"The document has more than {MaxEntries * 10} styles.");
        }
    }

    private void Warn(ConversionWarningKind kind, string message)
    {
        warnings[(kind, message)] = warnings.GetValueOrDefault((kind, message)) + 1;
    }

    /// <summary>The master page the first paragraph or table of the body asks for, if any.</summary>
    private string? FirstMaster(XElement body)
    {
        foreach (XElement element in body.Descendants())
        {
            if (element.Name == TextNs + "p" || element.Name == TextNs + "h")
            {
                return MasterOf(Find("paragraph", element.Attr(TextNs, "style-name")));
            }

            if (element.Name == TableNs + "table")
            {
                return MasterOf(Find("table", element.Attr(TableNs, "style-name")));
            }
        }

        return null;
    }

    private string? MasterOf(OdfStyle? style)
    {
        foreach (OdfStyle s in Chain(style))
        {
            if (s.Element.Attr(StyleNs, "master-page-name") is { } master)
            {
                return master.Length > 0 && masterPages.ContainsKey(master) ? master : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Starts a new section where a paragraph or table asks for a master page (ODF 1.3 Part 3, 19.507): the model's sections
    /// are ODF's page style changes.
    /// </summary>
    private void BreakForMaster(OdfStyle? style, string family, BlockContext context)
    {
        if (!context.Main || MasterOf(style) is not { } master)
        {
            return;
        }

        if (context.Blocks.Count > 0 || master != currentMaster)
        {
            StartSection(context, SectionStart.NewPage, master);
        }

        string? pageNumber = Inherited(style, family, family + "-properties", StyleNs, "page-number");
        if (OdfUnits.Int(pageNumber) is >= 0 and var number)
        {
            section.PageSetup.PageNumberStart = Math.Min(number, 32767);
        }
    }

    private void StartSection(BlockContext context, SectionStart start, string? master)
    {
        Section next;
        if (section.Body.Blocks.Count == 0)
        {
            // Nothing in the current section yet: it becomes the new one.
            next = section;
            if (master is not null)
            {
                next.HeadersFooters.Clear();
                next.PageSetup.DifferentFirstPage = null;
            }
        }
        else
        {
            next = document.Sections.AddTrusted(new Section(document));
            if (master is null)
            {
                next.PageSetup.CopyRaw(section.PageSetup);
                next.PageSetup.PageNumberStart = null;
                next.PageSetup.DifferentFirstPage = null;
            }
        }

        if (master is not null)
        {
            ApplyMaster(next, master);
            currentMaster = master;
        }

        next.PageSetup.SectionStart = start;
        section = next;
        context.Blocks = next.Body.Blocks;
    }

    /// <summary>Reads a story's blocks (a header, note, comment, cell or text box) with its own pending state.</summary>
    private void ReadStory(XElement container, NodeCollection<Block> blocks)
    {
        Pending saved = pending;
        bool savedInLink = inLink;
        pending = new Pending();
        inLink = false;
        try
        {
            BlockContext context = new(blocks, main: false);
            ReadBlocks(container, context, null);
            FinishStory(context);
        }
        finally
        {
            pending = saved;
            inLink = savedInLink;
        }
    }

    private void FinishStory(BlockContext context)
    {
        if (pending.Inlines.Count > 0 || pending.Before is not null || context.Blocks.Count == 0 || context.Blocks[^1] is not Paragraph)
        {
            // The paragraph a story ends with, as Word needs one: in the default style, as a paragraph without one is written.
            Paragraph paragraph = new(document) { Style = document.Styles.DefaultParagraphStyle };
            AddBlock(context, paragraph);
            foreach (Inline inline in pending.Inlines)
            {
                paragraph.Inlines.AddTrusted(inline);
            }

            pending.Inlines.Clear();
        }
    }

    private void AddBlock(BlockContext context, Block block)
    {
        if (pending.Before is { } before)
        {
            OdtExtension.Of(block).Before = before;
            pending.Before = null;
        }

        context.Blocks.AddTrusted(block);
    }

    private void ReadBlocks(XElement container, BlockContext context, ListState? list)
    {
        Enter();
        try
        {
            foreach (XElement element in container.Elements())
            {
                ReadBlock(element, context, list);
            }
        }
        finally
        {
            depth--;
        }
    }

    private void ReadBlock(XElement element, BlockContext context, ListState? list)
    {
        XNamespace ns = element.Name.Namespace;
        string name = element.Name.LocalName;
        if (ns == TextNs)
        {
            switch (name)
            {
                case "p" or "h":
                    ReadParagraph(element, context, list);
                    return;
                case "list":
                    ReadList(element, context, list);
                    return;
                case "numbered-paragraph":
                    foreach (XElement p in element.Elements().Where(e => e.Name == TextNs + "p" || e.Name == TextNs + "h"))
                    {
                        ReadParagraph(p, context, list);
                    }

                    return;
                case "section":
                    ReadSection(element, context, list);
                    return;
                case "table-of-content" or "illustration-index" or "table-index" or "object-index" or "user-index"
                    or "alphabetical-index" or "bibliography":
                    ReadIndex(element, context, list);
                    return;
                case "index-body" or "index-title":
                    ReadBlocks(element, context, list);
                    return;
                case "soft-page-break" or "tracked-changes":
                    return;
                case "change" or "change-start" or "change-end":
                    ReadBlockChange(element, context, list);
                    return;
                case "sequence-decls" or "variable-decls" or "user-field-decls" or "dde-connection-decls" or "alphabetical-index-auto-mark-file":
                    if (context.Main)
                    {
                        declarations.Add(element);
                    }

                    return;
            }
        }
        else if (ns == TableNs && name == "table")
        {
            ReadTable(element, context);
            return;
        }
        else if (ns == DrawNs)
        {
            // A frame anchored to the page sits in the body between paragraphs; it goes into the next paragraph.
            if (ReadDrawing(element, null) is { } drawing)
            {
                pending.Inlines.Add(drawing);
            }

            return;
        }
        else if (ns == DcNs || ns == MetaNs || (ns == OfficeNs && name is "annotation" or "annotation-end"))
        {
            // An annotation's author and date are read with it.
            return;
        }
        else if (ns == OfficeNs && name is "forms" or "scripts" or "event-listeners")
        {
            if (context.Main)
            {
                declarations.Add(element);
            }

            return;
        }

        (pending.Before ??= []).Add(element);
    }

    private void ReadParagraph(XElement p, BlockContext context, ListState? list)
    {
        Enter();
        try
        {
            OdfStyle? style = Find("paragraph", p.Attr(TextNs, "style-name"));
            BreakForMaster(style, "paragraph", context);
            Paragraph paragraph = new(document);
            AddBlock(context, paragraph);
            RunFormat run = new();
            OdfStyle? common = CommonOf(style);
            ApplyAutomatic(style, paragraph.Format, run, SizeOf(common is null ? null : ParentOf(common)));
            if (common?.Model is ParagraphStyle paragraphStyle)
            {
                paragraph.Style = paragraphStyle;
            }

            if (run.GetRaw(RunFormat.SizeId) is null && run.SizeComplexScript is null && !HasAny(run))
            {
                run = new RunFormat();
            }
            else
            {
                paragraph.MarkFormat.CopyRaw(run);
            }

            if (p.Name == TextNs + "h")
            {
                ApplyHeading(p, paragraph, list);
            }

            if (list is not null)
            {
                if (list.Unnumbered)
                {
                    paragraph.Format.List = List.None;
                    if (list.List != List.None && list.List.GetLevel(list.Level).ParagraphFormat.LeftIndent is { } left && paragraph.Format.LeftIndent is null)
                    {
                        paragraph.Format.LeftIndent = left;
                        paragraph.Format.FirstLineIndent ??= 0;
                    }
                }
                else
                {
                    paragraph.Format.List = list.List;
                    paragraph.Format.ListLevel = list.Level;
                }
            }

            if (pending.PageBreak)
            {
                paragraph.Format.PageBreakBefore = true;
                pending.PageBreak = false;
            }

            foreach (Inline inline in pending.Inlines)
            {
                paragraph.Inlines.AddTrusted(inline);
            }

            pending.Inlines.Clear();
            string? breakBefore = Inherited(style, "paragraph", "paragraph-properties", FoNs, "break-before");
            if (breakBefore == "column")
            {
                paragraph.Inlines.AddTrusted(new Break(document, BreakKind.Column));
            }

            InlineContext inlines = new(paragraph, paragraph.Inlines, topLevel: true, context, new WhiteSpace());
            ReadInlines(p, inlines, run, null, run.Size ?? SizeOf(style));
            Flush(inlines);
            switch (Inherited(style, "paragraph", "paragraph-properties", FoNs, "break-after"))
            {
                case "page" or "even-page" or "odd-page":
                    pending.PageBreak = true;
                    break;
                case "column":
                    inlines.Paragraph.Inlines.AddTrusted(new Break(document, BreakKind.Column));
                    break;
            }

            if (CurrentRevision() is { } revision)
            {
                inlines.Paragraph.MarkRevision = revision;
            }
        }
        finally
        {
            depth--;
        }
    }

    private static bool HasAny(FormatBase format)
    {
        for (int id = 0; id < format.PropertyCount; id++)
        {
            if (format.GetRaw(id) is not null)
            {
                return true;
            }
        }

        return false;
    }

    private OdfStyle? CommonOf(OdfStyle? style) => Chain(style).FirstOrDefault(s => !s.Automatic);

    private void ApplyHeading(XElement h, Paragraph paragraph, ListState? list)
    {
        int level = Math.Clamp(OdfUnits.Int(h.Attr(TextNs, "outline-level")) ?? 1, 1, 10);
        int outline = Math.Min(level, 9) - 1;
        if (paragraph.Style?.ParagraphFormat.OutlineLevel != outline)
        {
            paragraph.Format.OutlineLevel = outline;
        }

        if (list is not null || outlineList is null || level > 9)
        {
            return;
        }

        if (h.Attr(TextNs, "is-list-header") == "true")
        {
            paragraph.Format.List = List.None;
        }
        else if (paragraph.Style?.ParagraphFormat.List != outlineList)
        {
            paragraph.Format.List = outlineList;
            paragraph.Format.ListLevel = level - 1;
        }
    }

    private void ReadList(XElement element, BlockContext context, ListState? outer)
    {
        Enter();
        try
        {
            int level = outer is null ? 0 : Math.Min(outer.Level + 1, 8);
            XElement? style = element.Attr(TextNs, "style-name") is { } name ? ListStyle(name) : null;
            List list;
            if (outer is not null && (style is null || style == outer.Style))
            {
                list = outer.List;
                style = outer.Style;
            }
            else if (style is null)
            {
                list = List.None;
            }
            else if (element.Attr(TextNs, "continue-list") is { } continued && listsById.TryGetValue(continued, out List? found))
            {
                list = found;
            }
            else if (element.Attr(TextNs, "continue-numbering") == "true" && lastListOfStyle.TryGetValue(style, out List? last))
            {
                list = last;
            }
            else
            {
                list = NewList(DefinitionOf(style), restart: true);
            }

            if (style is not null && list != List.None)
            {
                lastListOfStyle[style] = list;
            }

            if (element.Attr(XmlNs, "id") is { } id && list != List.None && listsById.Count < MaxEntries)
            {
                listsById[id] = list;
            }

            foreach (XElement item in element.Elements())
            {
                bool header = item.Name == TextNs + "list-header";
                if (!header && item.Name != TextNs + "list-item")
                {
                    ReadBlock(item, context, outer);
                    continue;
                }

                if (!header && list != List.None && OdfUnits.Int(item.Attr(TextNs, "start-value")) is >= 0 and var start)
                {
                    list = NewList(list.Definition, restart: false);
                    list.Overrides[level].StartAt = Math.Min(start, 32767);
                    if (style is not null)
                    {
                        lastListOfStyle[style] = list;
                    }
                }

                bool numbered = !header && list != List.None;
                foreach (XElement child in item.Elements())
                {
                    if (child.Name == TextNs + "p" || child.Name == TextNs + "h")
                    {
                        // The item's first paragraph has the label; the ones after it continue the item without one.
                        ReadParagraph(child, context, new ListState(list, level, style, !numbered));
                        numbered = false;
                    }
                    else if (child.Name == TextNs + "list")
                    {
                        ReadList(child, context, new ListState(list, level, style, false));
                        numbered = false;
                    }
                    else
                    {
                        ReadBlock(child, context, new ListState(list, level, style, true));
                    }
                }
            }
        }
        finally
        {
            depth--;
        }
    }

    private XElement? ListStyle(string name)
    {
        return listScope.GetValueOrDefault(name) ?? commonListStyles.GetValueOrDefault(name);
    }

    // The ids of the lists and definitions read so far: the reader adds them with the ids it picks, as a scan for the next
    // free id on each would make a document of many lists slow.
    private int listIds;
    private int definitionIds;

    private List NewList(ListDefinition definition, bool restart)
    {
        if (++listCount > MaxEntries)
        {
            return lastListOfStyle.Values.FirstOrDefault(l => l.Definition == definition) ?? document.Lists.Add(++listIds, definition);
        }

        List list = document.Lists.Add(++listIds, definition);
        if (restart)
        {
            // A new list starts its numbers again (ODF 1.3 Part 3, 19.801): the model's lists continue others of the same
            // definition unless told to restart.
            for (int i = 0; i < 9; i++)
            {
                list.Overrides[i].StartAt = definition.Levels[i].StartAt;
            }
        }

        return list;
    }

    private void ReadSection(XElement element, BlockContext context, ListState? list)
    {
        OdfStyle? style = Find("section", element.Attr(TextNs, "style-name"));
        XElement? columns = Chain(style).Select(s => s.Properties("section-properties")?.Element(StyleNs + "columns")).FirstOrDefault(c => c is not null);
        int count = OdfUnits.Int(columns?.Attr(FoNs, "column-count")) ?? 1;
        if (!context.Main || list is not null || columns is null || count <= 1)
        {
            ReadBlocks(element, context, list);
            return;
        }

        // A section with columns is a continuous section of the model's, and the text after it another.
        PageSetup before = section.PageSetup;
        (int? Count, double? Spacing, IReadOnlyList<TextColumn>? Columns, bool? Equal, bool? Separator) saved =
            (before.ColumnCount, before.ColumnSpacing, before.Columns, before.EqualColumnWidths, before.ColumnSeparator);
        StartSection(context, SectionStart.Continuous, null);
        section.PageSetup.Columns = null;
        section.PageSetup.EqualColumnWidths = null;
        ApplyColumns(columns, section.PageSetup);
        if (Chain(style).Select(s => s.Properties("section-properties")?.Attr(TextNs, "dont-balance-text-columns")).FirstOrDefault(v => v is not null) == "true")
        {
            section.PageSetup.BalanceColumns = false;
        }

        ReadBlocks(element, context, list);
        StartSection(context, SectionStart.Continuous, null);
        section.PageSetup.ColumnCount = saved.Count;
        section.PageSetup.ColumnSpacing = saved.Spacing;
        section.PageSetup.Columns = saved.Columns;
        section.PageSetup.EqualColumnWidths = saved.Equal;
        section.PageSetup.ColumnSeparator = saved.Separator;
        section.PageSetup.BalanceColumns = null;
    }

    private void ReadInlines(XElement parent, InlineContext context, RunFormat format, CharacterStyle? characterStyle, double size)
    {
        Enter();
        try
        {
            foreach (XNode node in parent.Nodes())
            {
                if (node is XText text)
                {
                    AddText(context, text.Value, format, characterStyle, collapse: true);
                }
                else if (node is XElement element)
                {
                    ReadInline(element, context, format, characterStyle, size);
                }
            }
        }
        finally
        {
            depth--;
        }
    }

    private void ReadInline(XElement element, InlineContext context, RunFormat format, CharacterStyle? characterStyle, double size)
    {
        XNamespace ns = element.Name.Namespace;
        string name = element.Name.LocalName;
        if (ns == TextNs)
        {
            switch (name)
            {
                case "span":
                    {
                        (RunFormat spanFormat, CharacterStyle? spanStyle) = SpanFormat(element.Attr(TextNs, "style-name"), format, characterStyle, size);
                        ReadInlines(element, context, spanFormat, spanStyle, spanFormat.Size ?? size);
                        return;
                    }

                case "s":
                    AddText(context, new string(' ', Math.Clamp(OdfUnits.Int(element.Attr(TextNs, "c")) ?? 1, 1, 1000)), format, characterStyle, collapse: false);
                    return;
                case "tab":
                    AddInline(context, new Tab(document), format, characterStyle);
                    return;
                case "line-break":
                    AddInline(context, new Break(document, BreakKind.Line), format, characterStyle);
                    return;
                case "soft-page-break":
                    return;
                case "a":
                    ReadLink(element, context, format, characterStyle, size);
                    return;
                case "note":
                    ReadNote(element, context);
                    return;
                case "bookmark" or "reference-mark":
                    if (element.Attr(TextNs, "name") is { } point)
                    {
                        AddInline(context, new BookmarkStart(document, point), null, null);
                        AddInline(context, new BookmarkEnd(document, point), null, null);
                    }

                    return;
                case "bookmark-start" or "reference-mark-start":
                    if (element.Attr(TextNs, "name") is { } start)
                    {
                        AddInline<Inline>(context, Permission(start) is { } range ? EditableStart(range) : new BookmarkStart(document, start), null, null);
                    }

                    return;
                case "bookmark-end" or "reference-mark-end":
                    if (element.Attr(TextNs, "name") is { } end)
                    {
                        AddInline<Inline>(context, Permission(end) is { } range ? new EditableRangeEnd(document, range.Id) : new BookmarkEnd(document, end), null, null);
                    }

                    return;
                case "change-start" or "change-end" or "change":
                    ReadInlineChange(element, context, format, characterStyle, size);
                    return;
                case "ruby":
                    if (element.Element(TextNs + "ruby-base") is { } rubyBase)
                    {
                        ReadInlines(rubyBase, context, format, characterStyle, size);
                    }

                    return;
                case "meta" or "meta-field" or "placeholder" or "text-input" or "bibliography-mark":
                    ReadInlines(element, context, format, characterStyle, size);
                    return;
                case "hidden-text":
                    {
                        RunFormat hidden = Copy(format);
                        hidden.Hidden = element.Attr(TextNs, "is-hidden") != "false";
                        ReadInlines(element, context, hidden, characterStyle, size);
                        return;
                    }

                case "conditional-text":
                    {
                        bool condition = element.Attr(TextNs, "current-value") == "true";
                        string? shown = element.Attr(TextNs, condition ? "string-value-if-true" : "string-value-if-false");
                        AddText(context, shown ?? element.Value, format, characterStyle, collapse: false);
                        return;
                    }

                case "toc-mark" or "toc-mark-start" or "toc-mark-end" or "user-index-mark" or "user-index-mark-start" or "user-index-mark-end"
                    or "alphabetical-index-mark" or "alphabetical-index-mark-start" or "alphabetical-index-mark-end":
                    KeepInline(context, element);
                    return;
            }

            if (ReadField(element, context, format, characterStyle))
            {
                return;
            }
        }
        else if (ns == OfficeNs && name == "annotation")
        {
            ReadComment(element, context);
            return;
        }
        else if (ns == OfficeNs && name == "annotation-end")
        {
            if (element.Attr(OfficeNs, "name") is { } commentName && openComments.Remove(commentName, out (Comment Comment, Paragraph) open))
            {
                AddInline(context, new CommentRangeEnd(document, open.Comment), null, null);
                AddInline(context, open.Comment, null, null);
            }

            return;
        }
        else if (ns == DrawNs)
        {
            if (context.TopLevel && ReadFloatingTable(element, context))
            {
                return;
            }

            if (ReadDrawing(element, context) is { } drawing)
            {
                AddInline(context, drawing, null, null);
            }

            return;
        }

        // Markup the reader does not know: its text is read; markup without text (LibreOffice's field marks with their
        // parameters, say) is kept whole for the writer.
        if (element.DescendantNodes().Any(n => n is XText || (n is XElement e && e.Name.Namespace == TextNs)))
        {
            ReadInlines(element, context, format, characterStyle, size);
        }
        else
        {
            KeepInline(context, element);
        }
    }

    /// <summary>
    /// A bookmark LibreOffice writes for a range Word lets someone edit in a protected document: "permission-for-group:id:group"
    /// or "permission-for-user:id:user"; null for other bookmarks.
    /// </summary>
    private static (int Id, EditorGroup Group, string? Editor)? Permission(string name)
    {
        string[] parts = name.Split(':', 3);
        if (parts.Length != 3 || !int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int id))
        {
            return null;
        }

        if (parts[0] == "permission-for-user" && parts[2].Length > 0)
        {
            return (id, EditorGroup.None, parts[2]);
        }

        return parts[0] == "permission-for-group" && Enum.TryParse(parts[2], ignoreCase: true, out EditorGroup group) && Enum.IsDefined(group) && group != EditorGroup.None
            ? (id, group, null)
            : null;
    }

    private EditableRangeStart EditableStart((int Id, EditorGroup Group, string? Editor) range)
    {
        return range.Editor is { } editor ? new EditableRangeStart(document, range.Id, editor) : new EditableRangeStart(document, range.Id, range.Group);
    }

    private void KeepInline(InlineContext context, XElement element)
    {
        Flush(context);
        if (context.TopLevel)
        {
            (OdtExtension.Of(context.Paragraph).Inline ??= []).Add((context.Inlines.Count, element));
        }
    }

    /// <summary>The run formatting and character style inside a span of style <paramref name="name"/>.</summary>
    private (RunFormat Format, CharacterStyle? Style) SpanFormat(string? name, RunFormat format, CharacterStyle? characterStyle, double size)
    {
        OdfStyle? style = Find("text", name);
        if (style is null)
        {
            return (format, characterStyle);
        }

        RunFormat result = Copy(format);
        OdfStyle? common = CommonOf(style);
        if (common?.Model is CharacterStyle span)
        {
            // The span's character style applies over the paragraph's text formatting: what the style sets, the formatting
            // under it no longer does. The span's own automatic style is direct formatting over both.
            foreach (Style s in span.BasedOnChain())
            {
                RunFormat given = ((CharacterStyle)s).RunFormat;
                for (int id = 0; id < RunFormat.Count; id++)
                {
                    if (given.GetRaw(id) is not null)
                    {
                        result.SetRawSilently(id, null);
                    }
                }
            }

            characterStyle = span;
        }

        ApplyAutomatic(style, null, result, size);
        return (result, characterStyle);
    }

    private static RunFormat Copy(RunFormat format)
    {
        RunFormat copy = new();
        copy.CopyRaw(format);
        return copy;
    }

    private void AddText(InlineContext context, string text, RunFormat format, CharacterStyle? characterStyle, bool collapse)
    {
        Revision? revision = CurrentRevision();
        if (context.Text.Length > 0 && (context.TextFormat != format || context.TextStyle != characterStyle || context.TextRevision != revision))
        {
            Flush(context);
        }

        context.TextFormat = format;
        context.TextStyle = characterStyle;
        context.TextRevision = revision;
        WhiteSpace space = context.Space;
        if (!collapse)
        {
            context.Text.Append(text);
            space.AtStart = false;
            space.LastSpace = false;
            return;
        }

        // ODF collapses white space as XML text does: runs of spaces, tabs and line ends are one space, none at the start
        // of a paragraph (ODF 1.3 Part 3, 6.1.2).
        foreach (char c in text)
        {
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                if (!space.AtStart && !space.LastSpace)
                {
                    context.Text.Append(' ');
                    space.LastSpace = true;
                }
            }
            else
            {
                context.Text.Append(c);
                space.AtStart = false;
                space.LastSpace = false;
            }
        }
    }

    private void Flush(InlineContext context)
    {
        if (context.Text.Length == 0)
        {
            return;
        }

        Run run = new(document, context.Text.ToString());
        if (context.TextFormat is { } format)
        {
            run.Format.CopyRaw(format);
        }

        run.Style = context.TextStyle;
        run.Revision = context.TextRevision;
        context.Inlines.AddTrusted(run);
        context.Text.Clear();
    }

    /// <summary>Adds an inline that is not text, with the run formatting given (for tabs, breaks and field runs) and the revision in force.</summary>
    private T AddInline<T>(InlineContext context, T inline, RunFormat? format, CharacterStyle? characterStyle)
        where T : Inline
    {
        Flush(context);
        if (format is not null && inline is Run run)
        {
            run.Format.CopyRaw(format);
            run.Style = characterStyle;
        }

        if (CurrentRevision() is { } revision && inline.Revision is null)
        {
            inline.Revision = revision;
        }

        context.Space.AtStart = false;
        context.Space.LastSpace = false;
        context.Inlines.AddTrusted(inline);
        return inline;
    }

    private void ReadLink(XElement element, InlineContext context, RunFormat format, CharacterStyle? characterStyle, double size)
    {
        (RunFormat linkFormat, CharacterStyle? linkStyle) = SpanFormat(element.Attr(TextNs, "style-name"), format, characterStyle, size);
        if (inLink)
        {
            // A link in a link, which ODF's schema forbids but files hold: the outer link wins, as hyperlinks do not nest.
            ReadInlines(element, context, linkFormat, linkStyle, size);
            return;
        }

        Hyperlink link = new(document);
        string? href = element.Attr(XLinkNs, "href");
        if (href is { Length: > 1 } && href[0] == '#')
        {
            link.Anchor = href[1..];
        }
        else if (!string.IsNullOrEmpty(href))
        {
            link.Address = href;
        }

        link.Target = element.Attr(OfficeNs, "target-frame-name") is { } frame && frame != "_self" ? frame : null;
        link.Tooltip = element.Attr(OfficeNs, "title");
        AddInline(context, link, null, null);
        InlineContext inner = new(context.Paragraph, link.Inlines, topLevel: false, context.Blocks, context.Space);
        inLink = true;
        try
        {
            ReadInlines(element, inner, linkFormat, linkStyle, size);
            Flush(inner);
        }
        finally
        {
            inLink = false;
        }
    }

    /// <summary>True while a link's text is read: hyperlinks do not nest.</summary>
    private bool inLink;

    private void ReadNote(XElement element, InlineContext context)
    {
        Footnote note = new(document, element.Attr(TextNs, "note-class") == "endnote" ? NoteKind.Endnote : NoteKind.Footnote);
        if (element.Element(TextNs + "note-citation") is { } citation && citation.Attr(TextNs, "label") is { Length: > 0 } label)
        {
            note.CustomMark = label;
        }

        string? id = element.Attr(TextNs, "id");
        bool marked = id is not null && referencedNotes.Contains(id);
        if (marked)
        {
            AddInline(context, new BookmarkStart(document, id!), null, null);
        }

        AddInline(context, note, null, null);
        if (marked)
        {
            AddInline(context, new BookmarkEnd(document, id!), null, null);
        }

        if (element.Element(TextNs + "note-body") is { } body)
        {
            ReadStory(body, note.Story.Blocks);
        }
        else
        {
            note.Story.Blocks.AddTrusted(new Paragraph(document));
        }
    }

    private void ReadComment(XElement element, InlineContext context)
    {
        if (openComments.Count + document.Sections.Count > MaxEntries * 10)
        {
            return;
        }

        string author = element.Element(DcNs + "creator")?.Value ?? "";
        Comment comment = new(document, author, OdfUnits.Date(element.Element(DcNs + "date")?.Value))
        {
            Initials = element.Element(MetaNs + "creator-initials")?.Value ?? element.Element(LoExtNs + "sender-initials")?.Value,
            Done = element.Attr(LoExtNs, "resolved") == "true",
        };
        ReadStory(element, comment.Story.Blocks);
        if (element.Attr(OfficeNs, "name") is { } name && !openComments.ContainsKey(name))
        {
            AddInline(context, new CommentRangeStart(document, comment), null, null);
            openComments[name] = (comment, context.Paragraph);
        }
        else
        {
            AddInline(context, comment, null, null);
        }
    }

    // A comment whose end never came ends with the paragraph it started in.
    private void CloseComments()
    {
        foreach ((Comment comment, Paragraph paragraph) in openComments.Values)
        {
            paragraph.Inlines.AddTrusted(new CommentRangeEnd(document, comment));
            paragraph.Inlines.AddTrusted(comment);
        }

        openComments.Clear();
    }

    private void ReadIndex(XElement element, BlockContext context, ListState? list)
    {
        string? code = null;
        if (element.Name == TextNs + "table-of-content")
        {
            XElement? source = element.Element(TextNs + "table-of-content-source");
            int levels = Math.Clamp(OdfUnits.Int(source?.Attr(TextNs, "outline-level")) ?? 10, 1, 9);
            code = $"TOC \\o \"1-{levels}\" \\h \\z \\u";
        }
        else if (element.Name == TextNs + "illustration-index" || element.Name == TextNs + "table-index")
        {
            string sourceName = element.Name.LocalName + "-source";
            string? sequence = element.Element(TextNs + sourceName)?.Attr(TextNs, "caption-sequence-name");
            code = sequence is { Length: > 0 } ? $"TOC \\h \\z \\c \"{sequence}\"" : null;
        }

        XElement? body = element.Element(TextNs + "index-body");
        if (body is null)
        {
            return;
        }

        if (body.Element(TextNs + "index-title") is { } title)
        {
            ReadBlocks(title, context, list);
        }

        int start = context.Blocks.Count;
        foreach (XElement child in body.Elements().Where(e => e.Name != TextNs + "index-title"))
        {
            ReadBlock(child, context, list);
        }

        if (code is null)
        {
            return;
        }

        // The index's entries are the result of a TOC field, as Word holds them.
        List<Paragraph> entries = [.. context.Blocks.Skip(start).OfType<Paragraph>()];
        if (entries.Count == 0)
        {
            Paragraph empty = new(document);
            AddBlock(context, empty);
            entries.Add(empty);
        }

        entries[0].Inlines.Insert(0, new FieldStart(document));
        entries[0].Inlines.Insert(1, new Run(document, code));
        entries[0].Inlines.Insert(2, new FieldSeparator(document));
        entries[^1].Inlines.AddTrusted(new FieldEnd(document));
    }
}
