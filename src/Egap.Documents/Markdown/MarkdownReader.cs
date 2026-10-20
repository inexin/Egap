using System.Globalization;
using System.Text;

namespace Egap.Documents.Markdown;

/// <summary>One container a Markdown block sat in: a block quote or a list item, outermost first.</summary>
/// <param name="Kind">'Q' for a block quote, 'I' for a list item.</param>
/// <param name="Id">Tells containers apart: blocks in the same quote or item share it.</param>
/// <param name="Marker">For an item, its marker as written, such as "-" or "3.".</param>
/// <param name="Tight">For an item, whether its list was tight.</param>
/// <param name="List">For an item, which list it belongs to, to keep neighbouring lists apart.</param>
internal sealed record MarkdownFrame(char Kind, int Id, string Marker = "", bool Tight = true, int List = 0);

/// <summary>What the Markdown reader keeps on a block for the Markdown writer: its kind, details and containers.</summary>
/// <param name="Kind">"p", "hr", "code", "html", "table", "front-matter", "list".</param>
/// <param name="Info">A code block's info string, or the front matter text.</param>
/// <param name="Tight">For a list paragraph, whether its list was tight.</param>
/// <param name="Marker">For a list paragraph, the bullet character or ordered delimiter.</param>
internal sealed record MarkdownExtension(string Kind, string? Info = null, bool? Tight = null, char? Marker = null)
{
    public const string Format = "markdown";

    /// <summary>The quotes and list items around the block, outermost first.</summary>
    public IReadOnlyList<MarkdownFrame> Frames { get; init; } = [];
}

/// <summary>
/// Maps the Markdown syntax tree onto the document model (Phase 6 design Q17): headings to Heading 1 to 6, emphasis to run
/// formatting, code to the HTML Code and HTML Preformatted styles, block quotes to a Block Quote style indented per level,
/// lists to Word list definitions built from their nesting, tables to Table Grid tables, footnotes to notes, raw HTML to
/// islands kept as text (6.5's reader takes them later).
/// </summary>
internal sealed class MarkdownReader
{
    private const double LevelIndent = 36;
    private readonly Document document;
    private readonly MarkdownLoadOptions options;
    private int notesInProgress;

    private Run? openRun;

    private readonly StringBuilder openText = new();

    /// <summary>How many hyperlinks the inlines being added sit in: a link (an autolink, say) inside a link keeps only its text.</summary>
    private int linksInProgress;
    private int nextFrameId = 1;

    private MarkdownReader(Document document, MarkdownLoadOptions options)
    {
        this.document = document;
        this.options = options;
    }

    public static Document Build(MdDocument syntax, MarkdownLoadOptions options)
    {
        Document document = new();
        MarkdownReader reader = new(document, options);
        if (syntax.FrontMatter is { } front)
        {
            reader.ReadFrontMatter(front);
        }

        Section section = document.AddSection();
        reader.AddBlocks(syntax, new Context(section.Body, 0, null, 0, null, []));
        reader.FlushRun();
        return document;
    }

    /// <summary>Where blocks go: the container, the block quote depth, the list and level they are in, the extra indent.</summary>
    private sealed record Context(BlockContainer Container, int QuoteDepth, ListState? List, int Level, MdList? MdList, MarkdownFrame[] Frames)
    {
        public double Indent => (List is null ? 0 : LevelIndent * (Level + 1)) + (QuoteDepth * LevelIndent);
    }

    /// <summary>One Markdown list tree: its Word list and which levels have been set up.</summary>
    private sealed class ListState(List list)
    {
        public List List { get; } = list;

        public bool[] Configured { get; } = new bool[9];
    }

    private void AddBlocks(MdNode parent, Context context)
    {
        foreach (MdNode child in parent.Children)
        {
            int before = context.Container.Blocks.Count;
            AddBlock(child, context);
            MarkFrames(context, before);
        }
    }

    /// <summary>Gives the blocks added since <paramref name="before"/> the context's containers, unless a deeper context did.</summary>
    private static void MarkFrames(Context context, int before)
    {
        NodeCollection<Block> blocks = context.Container.Blocks;
        for (int i = before; i < blocks.Count; i++)
        {
            Block block = blocks[i];
            MarkdownExtension? existing = block.Extensions.Get<MarkdownExtension>(MarkdownExtension.Format);
            if (existing is { Frames.Count: > 0 })
            {
                continue;
            }

            string kind = block switch { Table => "table", BlockIsland => "html", _ => "p" };
            block.Extensions.Set(MarkdownExtension.Format, (existing ?? new MarkdownExtension(kind)) with { Frames = context.Frames });
        }
    }

    private void AddBlock(MdNode node, Context context)
    {
        switch (node)
        {
            case MdParagraph p:
                AddInlines(NewParagraph(context), p);
                break;
            case MdHeading h:
                {
                    Paragraph paragraph = NewParagraph(context);
                    paragraph.Style = (ParagraphStyle)document.Styles.AddBuiltIn(BuiltInStyle.Heading1 + (h.Level - 1));
                    AddInlines(paragraph, h);
                    break;
                }

            case MdThematicBreak:
                {
                    Paragraph paragraph = NewParagraph(context);
                    paragraph.Format.BorderBottom = new Border(BorderStyle.Single, 0.75, DocumentColor.FromHex("#A0A0A0"), 1);
                    paragraph.Extensions.Set(MarkdownExtension.Format, new MarkdownExtension("hr"));
                    break;
                }

            case MdCodeBlock code:
                {
                    Paragraph paragraph = NewParagraph(context);
                    paragraph.Style = (ParagraphStyle)document.Styles.AddBuiltIn(BuiltInStyle.HtmlPreformatted);
                    string[] lines = (code.Literal.EndsWith('\n') ? code.Literal[..^1] : code.Literal).Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (i > 0)
                        {
                            paragraph.AddBreak();
                        }

                        if (lines[i].Length > 0)
                        {
                            paragraph.AddRun(lines[i]);
                        }
                    }

                    paragraph.Extensions.Set(MarkdownExtension.Format, new MarkdownExtension("code", code.Fenced ? code.Info : null));
                    break;
                }

            case MdHtmlBlock html:
                context.Container.Blocks.Add(new BlockIsland(document, "html", html.Literal));
                break;
            case MdBlockQuote quote:
                AddBlocks(quote, context with { QuoteDepth = context.QuoteDepth + 1, Frames = [.. context.Frames, new MarkdownFrame('Q', nextFrameId++)] });
                break;
            case MdList list:
                AddList(list, context);
                break;
            case MdTable table:
                AddTable(table, context);
                break;
        }
    }

    private Paragraph NewParagraph(Context context)
    {
        Paragraph paragraph = context.Container.AddParagraph();
        if (context.QuoteDepth > 0)
        {
            paragraph.Style = BlockQuoteStyle();
        }

        if (context.Indent > 0)
        {
            paragraph.Format.LeftIndent = context.Indent;
        }

        return paragraph;
    }

    private ParagraphStyle BlockQuoteStyle()
    {
        if (document.Styles["BlockQuote"] is ParagraphStyle existing)
        {
            return existing;
        }

        ParagraphStyle style = document.Styles.AddParagraphStyle("BlockQuote", "Block Quote");
        style.BasedOn = document.Styles.DefaultParagraphStyle;
        style.ParagraphFormat.LeftIndent = LevelIndent;
        style.ParagraphFormat.BorderLeft = new Border(BorderStyle.Single, 2.25, DocumentColor.FromHex("#D0D7DE"), 8);
        style.RunFormat.Color = DocumentColor.FromHex("#59636E");
        return style;
    }

    private void AddList(MdList mdList, Context context)
    {
        ListState state;
        int level;
        if (context.List is null)
        {
            state = new ListState(document.Lists.Add(document.Lists.AddDefinition()));
            level = 0;
        }
        else
        {
            state = context.List;
            level = Math.Min(context.Level + 1, 8);
        }

        ConfigureLevel(state, level, mdList);
        int listId = nextFrameId++;
        int number = mdList.Start;
        foreach (MdNode itemNode in mdList.Children)
        {
            MdListItem item = (MdListItem)itemNode;
            string marker = mdList.Ordered ? number++.ToString(CultureInfo.InvariantCulture) + mdList.Delimiter : mdList.BulletChar.ToString();
            MarkdownFrame frame = new('I', nextFrameId++, marker, mdList.Tight, listId);
            Context inner = context with { List = state, Level = level, MdList = mdList, Frames = [.. context.Frames, frame] };
            bool first = true;
            foreach (MdNode child in item.Children)
            {
                if (first && child is MdParagraph p)
                {
                    Paragraph paragraph = ListParagraph(context, state, level, mdList, item, inner.Frames);
                    AddInlines(paragraph, p);
                }
                else if (first)
                {
                    ListParagraph(context, state, level, mdList, item, inner.Frames);
                    int before = inner.Container.Blocks.Count;
                    AddBlock(child, inner);
                    MarkFrames(inner, before);
                }
                else
                {
                    int before = inner.Container.Blocks.Count;
                    AddBlock(child, inner);
                    MarkFrames(inner, before);
                    foreach (Paragraph added in inner.Container.Blocks.Skip(before).OfType<Paragraph>().Where(x => x.Format.List is null))
                    {
                        added.Style ??= (ParagraphStyle)document.Styles.AddBuiltIn(BuiltInStyle.ListParagraph);
                        added.Format.ContextualSpacing = mdList.Tight ? null : false;
                    }
                }

                first = false;
            }

            if (item.Children.Count == 0)
            {
                ListParagraph(context, state, level, mdList, item, inner.Frames);
            }
        }
    }

    private Paragraph ListParagraph(Context context, ListState state, int level, MdList mdList, MdListItem item, MarkdownFrame[] frames)
    {
        Paragraph paragraph = context.Container.AddParagraph();
        paragraph.Style = (ParagraphStyle)document.Styles.AddBuiltIn(BuiltInStyle.ListParagraph);
        paragraph.SetList(state.List, level);
        if (context.QuoteDepth > 0)
        {
            paragraph.Format.LeftIndent = (LevelIndent * (level + 1)) + (context.QuoteDepth * LevelIndent);
        }

        if (!mdList.Tight)
        {
            paragraph.Format.ContextualSpacing = false;
        }

        paragraph.Extensions.Set(MarkdownExtension.Format, new MarkdownExtension("list", null, mdList.Tight, mdList.Ordered ? mdList.Delimiter : mdList.BulletChar) { Frames = frames });
        if (item.TaskChecked is bool done)
        {
            InlineContentControl box = new(document);
            box.Properties.Kind = ContentControlKind.CheckBox;
            box.Properties.IsChecked = done;
            box.AddRun(done ? "☒" : "☐");
            paragraph.Inlines.Add(box);
            paragraph.AddRun(" ");
        }

        return paragraph;
    }

    private static void ConfigureLevel(ListState state, int level, MdList mdList)
    {
        if (state.Configured[level])
        {
            return;
        }

        state.Configured[level] = true;
        ListLevel format = state.List.Definition.Levels[level];
        format.ParagraphFormat.LeftIndent = LevelIndent * (level + 1);
        format.ParagraphFormat.FirstLineIndent = -18;
        if (mdList.Ordered)
        {
            format.NumberStyle = NumberStyle.Decimal;
            format.LevelText = "%" + (level + 1) + mdList.Delimiter;
            format.StartAt = mdList.Start;
        }
        else
        {
            format.NumberStyle = NumberStyle.Bullet;
            (format.LevelText, format.RunFormat.FontName) = (level % 3) switch
            {
                0 => ("", "Symbol"),
                1 => ("o", "Courier New"),
                _ => ("", "Wingdings"),
            };
        }
    }

    private void AddTable(MdTable mdTable, Context context)
    {
        Table table = context.Container.Blocks.Add(new Table(document));
        table.Style = (TableStyle)document.Styles.AddBuiltIn(BuiltInStyle.TableGrid);
        foreach (MdNode rowNode in mdTable.Children)
        {
            MdTableRow mdRow = (MdTableRow)rowNode;
            Row row = table.Rows.Add(new Row(document));
            if (mdRow.Header)
            {
                row.Format.IsHeader = true;
            }

            foreach (MdNode cellNode in mdRow.Children)
            {
                MdTableCell mdCell = (MdTableCell)cellNode;
                Cell cell = row.Cells.Add(new Cell(document));
                Paragraph paragraph = cell.AddParagraph();
                paragraph.Format.Alignment = mdCell.Align switch
                {
                    MdAlign.Center => ParagraphAlignment.Center,
                    MdAlign.Right => ParagraphAlignment.Right,
                    MdAlign.Left => ParagraphAlignment.Left,
                    _ => null,
                };
                AddInlines(paragraph, mdCell, new RunFormat { Bold = mdRow.Header ? true : null });
            }
        }
    }

    private void AddInlines(Paragraph paragraph, MdNode parent, RunFormat? format = null)
    {
        FlushRun();
        AddInlines(paragraph.Inlines, parent, format ?? new RunFormat(), null);
        FlushRun();
    }

    private void AddInlines(NodeCollection<Inline> target, MdNode parent, RunFormat format, CharacterStyle? style)
    {
        foreach (MdNode node in parent.Children)
        {
            switch (node)
            {
                case MdText text:
                    AppendRun(target, text.Span, format, style);
                    break;
                case MdSoftBreak:
                    AppendRun(target, " ", format, style);
                    break;
                case MdHardBreak:
                    target.Add(new Break(document));
                    break;
                case MdCode code:
                    AppendRun(target, code.Literal, format, (CharacterStyle)document.Styles.AddBuiltIn(BuiltInStyle.HtmlCode));
                    break;
                case MdEmphasis:
                    AddInlines(target, node, With(format, f => f.Italic = true), style);
                    break;
                case MdStrong:
                    AddInlines(target, node, With(format, f => f.Bold = true), style);
                    break;
                case MdStrikethrough:
                    AddInlines(target, node, With(format, f => f.Strikethrough = true), style);
                    break;
                case MdLink link when linksInProgress > 0:
                    AddInlines(target, link, format, style);
                    break;
                case MdLink link:
                    {
                        Hyperlink hyperlink = new(document);
                        if (link.Destination.StartsWith('#'))
                        {
                            hyperlink.Anchor = link.Destination[1..];
                        }
                        else
                        {
                            hyperlink.Address = link.Destination;
                        }

                        hyperlink.Tooltip = link.Title;
                        target.Add(hyperlink);
                        linksInProgress++;
                        AddInlines(hyperlink.Inlines, link, format, (CharacterStyle)document.Styles.AddBuiltIn(BuiltInStyle.Hyperlink));
                        linksInProgress--;
                        break;
                    }

                case MdImage image:
                    {
                        ImageData data = options.ImageResolver?.Invoke(image.Destination) ?? new ImageData(ReadOnlyMemory<byte>.Empty, "application/octet-stream");
                        // A resolved image takes its own size, at 96 pixels an inch, as a browser shows it.
                        (double width, double height) = data.Bytes.IsEmpty ? (0, 0) : ImageSize.Intrinsic(data.Bytes);
                        Picture picture = new(document, data, width, height)
                        {
                            Description = MdHtmlRenderer.PlainText(image),
                            Title = image.Title,
                            SourceUrl = image.Destination,
                        };
                        target.Add(picture);
                        break;
                    }

                case MdHtmlInline html:
                    target.Add(new Island(document, IslandKind.Other, "html", html.Literal));
                    break;
                case MdFootnoteReference reference when notesInProgress > 0:
                    // Notes cannot hold notes (as in Word): a reference inside a note stays as its number, superscript.
                    AppendRun(target, reference.Definition.Number.ToString(CultureInfo.InvariantCulture), With(format, f => f.VerticalPosition = VerticalTextPosition.Superscript), style);
                    break;
                case MdFootnoteReference reference:
                    {
                        Footnote note = new(document);
                        notesInProgress++;
                        AddBlocks(reference.Definition, new Context(note.Story, 0, null, 0, null, []));
                        notesInProgress--;
                        if (note.Story.Blocks.Count == 0)
                        {
                            note.AddParagraph();
                        }

                        if (note.Story.Paragraphs.FirstOrDefault() is { } firstParagraph)
                        {
                            firstParagraph.Style ??= (ParagraphStyle)document.Styles.AddBuiltIn(BuiltInStyle.FootnoteText);
                        }
                        target.Add(note);
                        break;
                    }
            }
        }
    }

    /// <summary>Adds text as a run, joining it to the last run when that one has the same formatting and style.</summary>
    /// <summary>
    /// Adds text to the last run when it has the same style and formatting, else to a new run. The last run's text is
    /// gathered in <see cref="openText"/> and set once (<see cref="FlushRun"/>), not joined piece by piece.
    /// </summary>
    private void AppendRun(NodeCollection<Inline> target, ReadOnlySpan<char> text, RunFormat format, CharacterStyle? style)
    {
        if (target.Count > 0 && target[^1] is Run last && last.Style == style && SameFormat(last.Format, format))
        {
            if (last != openRun)
            {
                FlushRun();
                openRun = last;
                openText.Clear().Append(last.Text);
            }

            openText.Append(text);
            return;
        }

        FlushRun();
        Run run = new(document, text.ToString()) { Style = style };
        run.Format.Apply(format);
        target.Add(run);
        openRun = run;
        openText.Clear().Append(text);
    }

    /// <summary>Sets the text gathered for the last run.</summary>
    private void FlushRun()
    {
        if (openRun is not null)
        {
            if (openText.Length != openRun.Text.Length)
            {
                openRun.Text = openText.ToString();
            }

            openRun = null;
        }
    }

    private static bool SameFormat(RunFormat a, RunFormat b)
    {
        for (int i = 0; i < RunFormat.Count; i++)
        {
            if (!Equals(a.GetRaw(i), b.GetRaw(i)))
            {
                return false;
            }
        }

        return true;
    }

    private static RunFormat With(RunFormat format, Action<RunFormat> change)
    {
        RunFormat copy = new();
        copy.Apply(format);
        change(copy);
        return copy;
    }

    /// <summary>Keeps the front matter for the writer and maps title, author, date, description and keywords.</summary>
    private void ReadFrontMatter(string text)
    {
        document.Extensions.Set(MarkdownExtension.Format, new MarkdownExtension("front-matter", text));
        foreach (string line in text.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            string key = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim().Trim('"', '\'');
            switch (key)
            {
                case "title":
                    document.Properties.Title = value;
                    break;
                case "author":
                    document.Properties.Author = value;
                    break;
                case "description" or "summary":
                    document.Properties.Comments = value;
                    break;
                case "keywords" or "tags":
                    document.Properties.Keywords = value.Trim('[', ']');
                    break;
                case "subject":
                    document.Properties.Subject = value;
                    break;
                case "date" when DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date):
                    document.Properties.Created = date;
                    break;
            }
        }
    }
}
