using System.Globalization;
using System.Text;

using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

using MdFootnote = Markdig.Extensions.Footnotes.Footnote;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;

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
/// Maps Markdig's syntax tree onto the document model: headings to Heading 1 to 6, emphasis to run formatting, code to the
/// HTML Code and HTML Preformatted styles, block quotes to a Block Quote style indented per level, lists to Word list
/// definitions built from their nesting, tables to Table Grid tables, footnotes to notes, raw HTML to islands kept as text.
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

    /// <summary>Set after a task list's check box, so the space Markdig leaves before the item's text is not doubled.</summary>
    private bool afterTaskBox;

    private MarkdownReader(Document document, MarkdownLoadOptions options)
    {
        this.document = document;
        this.options = options;
    }

    public static Document Build(MarkdownDocument syntax, MarkdownLoadOptions options)
    {
        Document document = new();
        MarkdownReader reader = new(document, options);
        if (syntax.Count > 0 && syntax[0] is YamlFrontMatterBlock front)
        {
            reader.ReadFrontMatter(front.Lines.ToString());
        }

        Section section = document.AddSection();
        reader.AddBlocks(syntax, new Context(section.Body, 0, null, 0, null, []));
        reader.FlushRun();
        return document;
    }

    /// <summary>Where blocks go: the container, the block quote depth, the list and level they are in, the extra indent.</summary>
    private sealed record Context(BlockContainer Container, int QuoteDepth, ListState? List, int Level, ListBlock? MdList, MarkdownFrame[] Frames)
    {
        public double Indent => (List is null ? 0 : LevelIndent * (Level + 1)) + (QuoteDepth * LevelIndent);
    }

    /// <summary>One Markdown list tree: its Word list and which levels have been set up.</summary>
    private sealed class ListState(List list)
    {
        public List List { get; } = list;

        public bool[] Configured { get; } = new bool[9];
    }

    private void AddBlocks(ContainerBlock parent, Context context)
    {
        foreach (Markdig.Syntax.Block child in parent)
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

    private void AddBlock(Markdig.Syntax.Block node, Context context)
    {
        switch (node)
        {
            // Read once at the start, kept where the writer finds it; definitions and notes are reached from their references.
            case YamlFrontMatterBlock or LinkReferenceDefinitionGroup or LinkReferenceDefinition or FootnoteGroup or BlankLineBlock:
                break;
            case ParagraphBlock p:
                AddInlines(NewParagraph(context), p.Inline);
                break;
            case HeadingBlock h:
                {
                    Paragraph paragraph = NewParagraph(context);
                    paragraph.Style = (ParagraphStyle)document.Styles.AddBuiltIn(BuiltInStyle.Heading1 + (h.Level - 1));
                    AddInlines(paragraph, h.Inline);
                    break;
                }

            case ThematicBreakBlock:
                {
                    Paragraph paragraph = NewParagraph(context);
                    paragraph.Format.BorderBottom = new Border(BorderStyle.Single, 0.75, DocumentColor.FromHex("#A0A0A0"), 1);
                    paragraph.Extensions.Set(MarkdownExtension.Format, new MarkdownExtension("hr"));
                    break;
                }

            case CodeBlock code:
                {
                    Paragraph paragraph = NewParagraph(context);
                    paragraph.Style = (ParagraphStyle)document.Styles.AddBuiltIn(BuiltInStyle.HtmlPreformatted);
                    for (int i = 0; i < code.Lines.Count; i++)
                    {
                        if (i > 0)
                        {
                            paragraph.AddBreak();
                        }

                        ReadOnlySpan<char> line = code.Lines.Lines[i].Slice.AsSpan();
                        if (line.Length > 0)
                        {
                            paragraph.AddRun(line.ToString());
                        }
                    }

                    paragraph.Extensions.Set(MarkdownExtension.Format, new MarkdownExtension("code", code is FencedCodeBlock fenced ? InfoString(fenced) : null));
                    break;
                }

            case HtmlBlock html:
                context.Container.Blocks.Add(new BlockIsland(document, "html", html.Lines.ToString()));
                break;
            case QuoteBlock quote:
                AddBlocks(quote, context with { QuoteDepth = context.QuoteDepth + 1, Frames = [.. context.Frames, new MarkdownFrame('Q', nextFrameId++)] });
                break;
            case ListBlock list:
                AddList(list, context);
                break;
            case MdTable table:
                AddTable(table, context);
                break;
            case LeafBlock { Inline: { } inline }:
                AddInlines(NewParagraph(context), inline);
                break;
            case ContainerBlock container:
                AddBlocks(container, context);
                break;
        }
    }

    /// <summary>A fenced code block's info string as written: its first word and what follows it.</summary>
    private static string InfoString(FencedCodeBlock fenced)
    {
        return string.IsNullOrEmpty(fenced.Arguments) ? fenced.Info ?? "" : fenced.Info + " " + fenced.Arguments;
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

    private void AddList(ListBlock mdList, Context context)
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

        int start = StartOf(mdList);
        ConfigureLevel(state, level, mdList, start);
        int listId = nextFrameId++;
        int number = start;
        foreach (Markdig.Syntax.Block itemNode in mdList)
        {
            ListItemBlock item = (ListItemBlock)itemNode;
            string marker = mdList.IsOrdered ? number++.ToString(CultureInfo.InvariantCulture) + mdList.OrderedDelimiter : mdList.BulletType.ToString();
            MarkdownFrame frame = new('I', nextFrameId++, marker, !mdList.IsLoose, listId);
            Context inner = context with { List = state, Level = level, MdList = mdList, Frames = [.. context.Frames, frame] };
            bool first = true;
            foreach (Markdig.Syntax.Block child in item)
            {
                if (first && child is ParagraphBlock p)
                {
                    Paragraph paragraph = ListParagraph(context, state, level, mdList, p.Inline?.FirstChild is TaskList task ? task.Checked : null, inner.Frames);
                    AddInlines(paragraph, p.Inline);
                }
                else if (first)
                {
                    ListParagraph(context, state, level, mdList, null, inner.Frames);
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
                        added.Format.ContextualSpacing = mdList.IsLoose ? false : null;
                    }
                }

                first = false;
            }

            if (item.Count == 0)
            {
                ListParagraph(context, state, level, mdList, null, inner.Frames);
            }
        }
    }

    /// <summary>An ordered list's start number (CommonMark allows up to nine digits); 1 for a bullet list.</summary>
    private static int StartOf(ListBlock list)
    {
        return list.IsOrdered && int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out int start) ? start : 1;
    }

    private Paragraph ListParagraph(Context context, ListState state, int level, ListBlock mdList, bool? taskChecked, MarkdownFrame[] frames)
    {
        Paragraph paragraph = context.Container.AddParagraph();
        paragraph.Style = (ParagraphStyle)document.Styles.AddBuiltIn(BuiltInStyle.ListParagraph);
        paragraph.SetList(state.List, level);
        if (context.QuoteDepth > 0)
        {
            paragraph.Format.LeftIndent = (LevelIndent * (level + 1)) + (context.QuoteDepth * LevelIndent);
        }

        if (mdList.IsLoose)
        {
            paragraph.Format.ContextualSpacing = false;
        }

        paragraph.Extensions.Set(MarkdownExtension.Format, new MarkdownExtension("list", null, !mdList.IsLoose, mdList.IsOrdered ? mdList.OrderedDelimiter : mdList.BulletType) { Frames = frames });
        if (taskChecked is bool done)
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

    private static void ConfigureLevel(ListState state, int level, ListBlock mdList, int start)
    {
        if (state.Configured[level])
        {
            return;
        }

        state.Configured[level] = true;
        ListLevel format = state.List.Definition.Levels[level];
        format.ParagraphFormat.LeftIndent = LevelIndent * (level + 1);
        format.ParagraphFormat.FirstLineIndent = -18;
        if (mdList.IsOrdered)
        {
            format.NumberStyle = NumberStyle.Decimal;
            format.LevelText = "%" + (level + 1) + mdList.OrderedDelimiter;
            format.StartAt = start;
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
        foreach (Markdig.Syntax.Block rowNode in mdTable)
        {
            if (rowNode is not MdTableRow mdRow)
            {
                continue;
            }

            Row row = table.Rows.Add(new Row(document));
            if (mdRow.IsHeader)
            {
                row.Format.IsHeader = true;
            }

            int column = 0;
            foreach (Markdig.Syntax.Block cellNode in mdRow)
            {
                MdTableCell mdCell = (MdTableCell)cellNode;
                Cell cell = row.Cells.Add(new Cell(document));
                Paragraph paragraph = cell.AddParagraph();
                int index = mdCell.ColumnIndex >= 0 ? mdCell.ColumnIndex : column;
                paragraph.Format.Alignment = (index < mdTable.ColumnDefinitions.Count ? mdTable.ColumnDefinitions[index].Alignment : null) switch
                {
                    TableColumnAlign.Center => ParagraphAlignment.Center,
                    TableColumnAlign.Right => ParagraphAlignment.Right,
                    TableColumnAlign.Left => ParagraphAlignment.Left,
                    _ => null,
                };
                RunFormat format = new() { Bold = mdRow.IsHeader ? true : null };
                FlushRun();
                foreach (Markdig.Syntax.Block content in mdCell)
                {
                    if (content is LeafBlock { Inline: { } inline })
                    {
                        AddInlines(paragraph.Inlines, inline, format, null);
                    }
                }

                FlushRun();
                column++;
            }
        }
    }

    private void AddInlines(Paragraph paragraph, ContainerInline? parent, RunFormat? format = null)
    {
        FlushRun();
        if (parent is not null)
        {
            AddInlines(paragraph.Inlines, parent, format ?? new RunFormat(), null);
        }

        FlushRun();
    }

    private void AddInlines(NodeCollection<Inline> target, ContainerInline parent, RunFormat format, CharacterStyle? style)
    {
        for (Markdig.Syntax.Inlines.Inline? node = parent.FirstChild; node is not null; node = node.NextSibling)
        {
            switch (node)
            {
                case LiteralInline text:
                    {
                        ReadOnlySpan<char> span = text.Content.AsSpan();
                        if (afterTaskBox)
                        {
                            span = span.StartsWith(' ') ? span[1..] : span;
                            afterTaskBox = false;
                        }

                        if (!span.IsEmpty)
                        {
                            AppendRun(target, span, format, style);
                        }

                        break;
                    }

                case HtmlEntityInline entity:
                    AppendRun(target, entity.Transcoded.AsSpan(), format, style);
                    break;
                case LineBreakInline { IsHard: true }:
                    target.Add(new Break(document));
                    break;
                case LineBreakInline:
                    AppendRun(target, " ", format, style);
                    break;
                case CodeInline code:
                    AppendRun(target, code.Content, format, (CharacterStyle)document.Styles.AddBuiltIn(BuiltInStyle.HtmlCode));
                    break;
                case TaskList:
                    // The list paragraph already holds the check box.
                    afterTaskBox = true;
                    break;
                case EmphasisInline { DelimiterChar: '~' } strike:
                    AddInlines(target, strike, With(format, f => f.Strikethrough = true), style);
                    break;
                case EmphasisInline { DelimiterCount: >= 2 } strong:
                    AddInlines(target, strong, With(format, f => f.Bold = true), style);
                    break;
                case EmphasisInline emphasis:
                    AddInlines(target, emphasis, With(format, f => f.Italic = true), style);
                    break;
                case LinkInline { IsImage: true } image:
                    {
                        string destination = image.Url ?? "";
                        ImageData data = options.ImageResolver?.Invoke(destination) ?? new ImageData(ReadOnlyMemory<byte>.Empty, "application/octet-stream");
                        // A resolved image takes its own size, at 96 pixels an inch, as a browser shows it.
                        (double width, double height) = data.Bytes.IsEmpty ? (0, 0) : ImageSize.Intrinsic(data.Bytes);
                        Picture picture = new(document, data, width, height)
                        {
                            Description = PlainText(image),
                            Title = string.IsNullOrEmpty(image.Title) ? null : image.Title,
                            SourceUrl = destination,
                        };
                        target.Add(picture);
                        break;
                    }

                case LinkInline link when linksInProgress > 0:
                    AddInlines(target, link, format, style);
                    break;
                case LinkInline link:
                    AddHyperlink(target, link.Url ?? "", string.IsNullOrEmpty(link.Title) ? null : link.Title, format, h => AddInlines(h.Inlines, link, format, HyperlinkStyle()));
                    break;
                case AutolinkInline auto when linksInProgress > 0:
                    AppendRun(target, auto.Url, format, style);
                    break;
                case AutolinkInline auto:
                    AddHyperlink(target, auto.IsEmail ? "mailto:" + auto.Url : auto.Url, null, format, h => AppendRun(h.Inlines, auto.Url, format, HyperlinkStyle()));
                    break;
                case HtmlInline html:
                    target.Add(new Island(document, IslandKind.Other, "html", html.Tag));
                    break;
                case FootnoteLink { IsBackLink: true }:
                    break;
                case FootnoteLink reference when notesInProgress > 0:
                    // Notes cannot hold notes (as in Word): a reference inside a note stays as its number, superscript.
                    AppendRun(target, reference.Footnote.Order.ToString(CultureInfo.InvariantCulture), With(format, f => f.VerticalPosition = VerticalTextPosition.Superscript), style);
                    break;
                case FootnoteLink reference:
                    AddFootnote(target, reference.Footnote);
                    break;
                case DelimiterInline delimiter:
                    // An opener nothing closed, such as a lone "[": its text, then what Markdig left inside it.
                    AppendRun(target, delimiter.ToLiteral(), format, style);
                    AddInlines(target, delimiter, format, style);
                    break;
                case ContainerInline container:
                    AddInlines(target, container, format, style);
                    break;
            }
        }
    }

    private CharacterStyle HyperlinkStyle() => (CharacterStyle)document.Styles.AddBuiltIn(BuiltInStyle.Hyperlink);

    private void AddHyperlink(NodeCollection<Inline> target, string destination, string? title, RunFormat format, Action<Hyperlink> addText)
    {
        Hyperlink hyperlink = new(document);
        if (destination.StartsWith('#'))
        {
            hyperlink.Anchor = destination[1..];
        }
        else
        {
            hyperlink.Address = destination;
        }

        hyperlink.Tooltip = title;
        target.Add(hyperlink);
        linksInProgress++;
        addText(hyperlink);
        linksInProgress--;
    }

    private void AddFootnote(NodeCollection<Inline> target, MdFootnote definition)
    {
        Footnote note = new(document);
        notesInProgress++;
        AddBlocks(definition, new Context(note.Story, 0, null, 0, null, []));
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
    }

    /// <summary>The text of an image's description, as its alt attribute: markup dropped.</summary>
    internal static string PlainText(ContainerInline node)
    {
        StringBuilder text = new();
        AppendPlainText(node, text);
        return text.ToString();
    }

    private static void AppendPlainText(ContainerInline node, StringBuilder text)
    {
        for (Markdig.Syntax.Inlines.Inline? child = node.FirstChild; child is not null; child = child.NextSibling)
        {
            switch (child)
            {
                case LiteralInline t:
                    text.Append(t.Content.AsSpan());
                    break;
                case HtmlEntityInline e:
                    text.Append(e.Transcoded.AsSpan());
                    break;
                case CodeInline c:
                    text.Append(c.Content);
                    break;
                case LineBreakInline:
                    text.Append('\n');
                    break;
                case AutolinkInline a:
                    text.Append(a.Url);
                    break;
                case ContainerInline inner:
                    AppendPlainText(inner, text);
                    break;
            }
        }
    }

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
