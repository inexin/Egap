namespace Egap.Documents;

/// <summary>
/// Writes into a document at a cursor (Phase 6 design Q3, as Aspose.Words' DocumentBuilder): text, paragraphs, breaks,
/// tables, pictures, fields, hyperlinks and bookmarks. The cursor sits in a paragraph, before an inline or at its end;
/// <see cref="Font"/> and <see cref="ParagraphFormat"/> give what is written its direct formatting.
/// </summary>
public sealed class DocumentBuilder
{
    private readonly Stack<TableState> tables = new();
    private readonly Stack<(ContentControl Group, Paragraph Paragraph, Inline? Before)> groups = new();
    private Paragraph paragraph;
    private Inline? before;

    /// <summary>A builder with its cursor at the end of the document, adding a section and a paragraph when it has none.</summary>
    public DocumentBuilder(Document document)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        paragraph = null!;
        MoveToDocumentEnd();
    }

    /// <summary>The document being written.</summary>
    public Document Document { get; }

    /// <summary>Character formatting for the text written from now on; changing it does not change text already written.</summary>
    public RunFormat Font { get; } = new();

    /// <summary>
    /// Paragraph formatting the cursor's paragraph gets and new paragraphs take with them; changes apply to the current
    /// paragraph when <see cref="ApplyParagraphFormat"/> is called and to each paragraph started after that.
    /// </summary>
    public ParagraphFormat ParagraphFormat { get; } = new();

    /// <summary>The paragraph the cursor is in.</summary>
    public Paragraph CurrentParagraph => paragraph;

    /// <summary>The inline the cursor is before, or null at the end of the paragraph.</summary>
    public Inline? CurrentNode => before;

    /// <summary>The style of the cursor's paragraph; setting it restyles that paragraph.</summary>
    public ParagraphStyle? ParagraphStyle
    {
        get => paragraph.Style;
        set => paragraph.Style = value;
    }

    /// <summary>Gives the cursor's paragraph the builder's <see cref="ParagraphFormat"/>.</summary>
    public void ApplyParagraphFormat()
    {
        paragraph.Format.Apply(ParagraphFormat);
    }

    /// <summary>Writes <paramref name="text"/> at the cursor in one run with <see cref="Font"/>; line breaks in it become paragraphs.</summary>
    public void Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                InsertParagraph();
            }

            if (lines[i].Length > 0)
            {
                Run run = new(Document, lines[i]);
                run.Format.Apply(Font);
                Put(run);
            }
        }
    }

    /// <summary>Writes <paramref name="text"/> and ends the paragraph.</summary>
    public void Writeln(string text = "")
    {
        Write(text);
        InsertParagraph();
    }

    /// <summary>
    /// Ends the paragraph at the cursor: what follows the cursor moves to a new paragraph after it, with the same style (or the
    /// style's next style) and direct formatting, and the cursor moves there.
    /// </summary>
    public Paragraph InsertParagraph()
    {
        Paragraph next = new(Document) { Style = paragraph.Style?.Next ?? paragraph.Style };
        next.Format.Apply(paragraph.Format);
        next.Format.Apply(ParagraphFormat);
        next.MarkFormat.Apply(paragraph.MarkFormat);

        // The inlines from the cursor on move along; inlines inside a hyperlink before the cursor stay in it.
        if (before is not null)
        {
            Inline top = TopLevel(before);
            List<Inline> moving = [];
            for (Node? node = top; node is Inline inline; node = node.NextSibling)
            {
                moving.Add(inline);
            }

            foreach (Inline inline in moving)
            {
                inline.Remove();
                next.Inlines.Add(inline);
            }
        }

        InsertBlockAfter(paragraph, next);
        paragraph = next;
        before = next.Inlines.Count > 0 ? next.Inlines[0] : null;
        return next;
    }

    /// <summary>Writes a break: a line or column break in the paragraph, a page break followed by a new paragraph as Word does.</summary>
    public void InsertBreak(BreakKind kind)
    {
        Put(new Break(Document, kind));
        if (kind == BreakKind.Page)
        {
            InsertParagraph();
        }
    }

    /// <summary>
    /// Starts a new section at the cursor: the paragraph is ended there, and it and what follows move to a new section after
    /// the current one, with a copy of its page setup and the given start.
    /// </summary>
    public Section InsertSectionBreak(SectionStart start = SectionStart.NewPage)
    {
        Section current = paragraph.GetAncestor<Section>() ?? throw new InvalidOperationException("Section breaks go in a section's body.");
        if (paragraph.Parent != current.Body)
        {
            throw new InvalidOperationException("Section breaks go in a section's body, not in a table or control.");
        }

        InsertParagraph();
        Section next = new(Document);
        next.PageSetup.Apply(current.PageSetup);
        next.PageSetup.SectionStart = start;
        List<Block> moving = [];
        for (Node? node = paragraph; node is Block block; node = node.NextSibling)
        {
            moving.Add(block);
        }

        foreach (Block block in moving)
        {
            block.Remove();
            next.Body.Blocks.Add(block);
        }

        Document.Sections.Insert(current.IndexInParent + 1, next);
        return next;
    }

    /// <summary>Writes a tab.</summary>
    public Tab InsertTab()
    {
        return Put(new Tab(Document));
    }

    /// <summary>Writes a picture of <paramref name="image"/> at <paramref name="width"/> by <paramref name="height"/> points.</summary>
    public Picture InsertImage(ImageData image, double width, double height)
    {
        return Put(new Picture(Document, image, width, height));
    }

    /// <summary>Writes a field with <paramref name="code"/> and the result to show until it is updated.</summary>
    public Field InsertField(string code, string result = "")
    {
        ArgumentNullException.ThrowIfNull(code);
        FieldStart start = Put(new FieldStart(Document));
        Run codeRun = new(Document, code.StartsWith(' ') ? code : " " + code + " ");
        Put(codeRun);
        Put(new FieldSeparator(Document));
        if (result.Length > 0)
        {
            Run resultRun = new(Document, result);
            resultRun.Format.Apply(Font);
            Put(resultRun);
        }

        Put(new FieldEnd(Document));
        return start.GetField()!;
    }

    /// <summary>Writes a hyperlink to <paramref name="address"/> showing <paramref name="text"/> in the Hyperlink style.</summary>
    public Hyperlink InsertHyperlink(string text, string address, bool isBookmark = false)
    {
        Hyperlink link = new(Document);
        if (isBookmark)
        {
            link.Anchor = address;
        }
        else
        {
            link.Address = address;
        }

        Run run = link.AddRun(text);
        run.Format.Apply(Font);
        run.Style = (CharacterStyle)Document.Styles.AddBuiltIn(BuiltInStyle.Hyperlink);
        return Put(link);
    }

    /// <summary>Writes the start of bookmark <paramref name="name"/>.</summary>
    public BookmarkStart StartBookmark(string name)
    {
        return Put(new BookmarkStart(Document, name));
    }

    /// <summary>Writes the end of bookmark <paramref name="name"/>.</summary>
    public BookmarkEnd EndBookmark(string name)
    {
        return Put(new BookmarkEnd(Document, name));
    }

    /// <summary>Writes a footnote (or endnote) holding <paramref name="text"/>.</summary>
    public Footnote InsertFootnote(string text, NoteKind kind = NoteKind.Footnote)
    {
        Footnote note = new(Document, kind);
        note.AddParagraph(text);
        return Put(note);
    }

    /// <summary>
    /// Starts a table before the cursor's paragraph; <see cref="InsertCell"/> adds cells, <see cref="EndRow"/> ends rows and
    /// <see cref="EndTable"/> returns the cursor to the paragraph. Tables nest when started inside a cell.
    /// </summary>
    public Table StartTable()
    {
        Table table = new(Document);
        switch (paragraph.Parent)
        {
            case BlockContainer container:
                container.Blocks.Insert(paragraph.IndexInParent, table);
                break;
            case ContentControl control:
                control.Blocks.Insert(paragraph.IndexInParent, table);
                break;
            default:
                throw new InvalidOperationException("Tables go where blocks go.");
        }

        tables.Push(new TableState(table, paragraph, before));
        return table;
    }

    /// <summary>Adds a cell to the current row (starting a row when needed) and moves the cursor into its paragraph.</summary>
    public Cell InsertCell()
    {
        TableState state = CurrentTable();
        if (state.Row is null)
        {
            state.Row = state.Table.Rows.Add(new Row(Document));
        }

        Cell cell = state.Row.AddCell();
        paragraph = cell.Paragraphs.First();
        paragraph.Format.Apply(ParagraphFormat);
        before = null;
        return cell;
    }

    /// <summary>Ends the current row; the next cell starts a new one.</summary>
    public Row EndRow()
    {
        TableState state = CurrentTable();
        Row row = state.Row ?? throw new InvalidOperationException("No row is open.");
        state.Row = null;
        return row;
    }

    /// <summary>Ends the table and moves the cursor back to the paragraph after it.</summary>
    public Table EndTable()
    {
        TableState state = CurrentTable();
        tables.Pop();
        paragraph = state.Paragraph;
        before = state.Before;
        return state.Table;
    }

    /// <summary>
    /// Starts a group of blocks before the cursor's paragraph and moves the cursor into it: what is written until
    /// <see cref="EndGroup"/> goes inside, and the group's <see cref="ContentControl.Paging"/> says how it is placed across
    /// pages (kept together, given room, shown entire). Groups nest.
    /// </summary>
    /// <example>
    /// <code>
    /// ContentControl totals = builder.StartGroup();
    /// totals.Paging = new GroupPaging { KeepTogether = true };
    /// builder.Writeln("Subtotal: 1 200");
    /// builder.Write("Total: 1 500");
    /// builder.EndGroup();
    /// </code>
    /// </example>
    public ContentControl StartGroup()
    {
        ContentControl group = new(Document);
        group.Properties.Kind = ContentControlKind.Group;
        switch (paragraph.Parent)
        {
            case BlockContainer container:
                container.Blocks.Insert(paragraph.IndexInParent, group);
                break;
            case ContentControl control:
                control.Blocks.Insert(paragraph.IndexInParent, group);
                break;
            default:
                throw new InvalidOperationException("Groups go where blocks go.");
        }

        groups.Push((group, paragraph, before));
        paragraph = group.AddParagraph();
        paragraph.Format.Apply(ParagraphFormat);
        before = null;
        return group;
    }

    /// <summary>Ends the innermost group and moves the cursor back to the paragraph after it.</summary>
    /// <exception cref="InvalidOperationException">No group is open.</exception>
    public ContentControl EndGroup()
    {
        if (!groups.TryPop(out (ContentControl Group, Paragraph Paragraph, Inline? Before) state))
        {
            throw new InvalidOperationException("No group is open; call StartGroup first.");
        }

        paragraph = state.Paragraph;
        before = state.Before;
        return state.Group;
    }

    /// <summary>Moves the cursor to the end of the document's last body paragraph, adding a section or paragraph when missing.</summary>
    public void MoveToDocumentEnd()
    {
        Section section = Document.Sections.Count > 0 ? Document.Sections[^1] : Document.AddSection();
        paragraph = section.Body.Paragraphs.LastOrDefault() ?? section.Body.AddParagraph();
        before = null;
    }

    /// <summary>Moves the cursor to the start of the document's first body paragraph.</summary>
    public void MoveToDocumentStart()
    {
        Section section = Document.Sections.Count > 0 ? Document.Sections[0] : Document.AddSection();
        paragraph = section.Body.Paragraphs.FirstOrDefault() ?? section.Body.AddParagraph();
        before = paragraph.Inlines.Count > 0 ? paragraph.Inlines[0] : null;
    }

    /// <summary>Moves the cursor to <paramref name="target"/>: before an inline, or to the end of a paragraph.</summary>
    public void MoveTo(Node target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Document != Document)
        {
            throw new ArgumentException("The node belongs to another document.", nameof(target));
        }

        switch (target)
        {
            case Paragraph p:
                paragraph = p;
                before = null;
                break;
            case Inline inline when inline.Paragraph is { } p:
                paragraph = p;
                before = inline;
                break;
            default:
                throw new ArgumentException("The cursor goes before an inline in a paragraph or to the end of a paragraph.", nameof(target));
        }
    }

    /// <summary>Moves the cursor to the end of the first section's header or footer of <paramref name="kind"/>, adding it when missing.</summary>
    public void MoveToHeaderFooter(HeaderFooterKind kind)
    {
        Section section = paragraph.GetAncestor<Section>() ?? (Document.Sections.Count > 0 ? Document.Sections[0] : Document.AddSection());
        HeaderFooter story = section.HeadersFooters.GetOrAdd(kind);
        paragraph = story.Paragraphs.LastOrDefault() ?? story.AddParagraph();
        before = null;
    }

    /// <summary>
    /// Moves the cursor to the end of the current section's margin box at <paramref name="position"/>, adding it when missing.
    /// </summary>
    /// <param name="position">Where the box sits.</param>
    /// <param name="firstPage">Whether the box is only for the section's first page.</param>
    public void MoveToMarginBox(MarginBoxPosition position, bool firstPage = false)
    {
        Section section = paragraph.GetAncestor<Section>() ?? (Document.Sections.Count > 0 ? Document.Sections[0] : Document.AddSection());
        MarginBox box = section.MarginBoxes.GetOrAdd(position, firstPage);
        if (box.Paragraphs.LastOrDefault() is { } last)
        {
            paragraph = last;
        }
        else
        {
            // A new box starts with the alignment CSS gives its position.
            paragraph = box.AddParagraph();
            paragraph.Format.Alignment = MarginBox.DefaultAlignment(position);
        }

        before = null;
    }

    /// <summary>Moves the cursor to the end of bookmark <paramref name="name"/> (or its start when it has no end).</summary>
    /// <returns>False when there is no such bookmark.</returns>
    public bool MoveToBookmark(string name)
    {
        if (Document.GetBookmark(name) is not { } bookmark)
        {
            return false;
        }

        Inline mark = (Inline?)bookmark.End ?? bookmark.Start;
        MoveTo(mark);
        return true;
    }

    private T Put<T>(T inline)
        where T : Inline
    {
        if (before is null)
        {
            paragraph.Inlines.Add(inline);
        }
        else
        {
            switch (before.Parent)
            {
                case Paragraph p:
                    p.Inlines.Insert(before.IndexInParent, inline);
                    break;
                case CompositeInline composite:
                    composite.Inlines.Insert(before.IndexInParent, inline);
                    break;
            }
        }

        return inline;
    }

    private Inline TopLevel(Inline inline)
    {
        Node node = inline;
        while (node.Parent is not null && node.Parent != paragraph)
        {
            node = node.Parent;
        }

        return (Inline)node;
    }

    private static void InsertBlockAfter(Paragraph paragraph, Paragraph next)
    {
        switch (paragraph.Parent)
        {
            case BlockContainer container:
                container.Blocks.Insert(paragraph.IndexInParent + 1, next);
                break;
            case ContentControl control:
                control.Blocks.Insert(paragraph.IndexInParent + 1, next);
                break;
            default:
                throw new InvalidOperationException("The cursor's paragraph is not in the tree.");
        }
    }

    private TableState CurrentTable()
    {
        return tables.Count > 0 ? tables.Peek() : throw new InvalidOperationException("No table is open; call StartTable first.");
    }

    private sealed class TableState(Table table, Paragraph paragraph, Inline? before)
    {
        public Table Table { get; } = table;

        public Paragraph Paragraph { get; } = paragraph;

        public Inline? Before { get; } = before;

        public Row? Row { get; set; }
    }
}
