using System.Text;
using System.Text.RegularExpressions;

namespace Egap.Documents.Markdown;

/// <summary>
/// The block phase of CommonMark parsing (spec 0.31, appendix "A parsing strategy"), with GitHub's tables, task list items
/// and footnote definitions: each line first continues the open blocks it can, then may start new ones, then its rest goes
/// to the deepest open block or starts a paragraph. Written from the spec; tabs count to the next multiple of four.
/// </summary>
internal sealed partial class BlockParser
{
    private const int CodeIndent = 4;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private readonly MdDocument document = new();
    private readonly MarkdownExtensions extensions;
    private string line = "";
    private int lineNumber;
    private int offset;
    private int column;
    private int nextNonspace;
    private int nextNonspaceColumn;
    private int indent;
    private bool indented;
    private bool blank;
    private bool partiallyConsumedTab;
    private bool allClosed = true;
    private bool lineConsumed;
    private MdBlock tip;
    private MdBlock oldTip;
    private MdBlock lastMatchedContainer;

    private BlockParser(MarkdownExtensions extensions)
    {
        this.extensions = extensions;
        tip = document;
        oldTip = document;
        lastMatchedContainer = document;
    }

    /// <summary>Parses <paramref name="markdown"/> into a syntax tree with its inlines.</summary>
    public static MdDocument Parse(string markdown, MarkdownExtensions extensions)
    {
        BlockParser parser = new(extensions);
        string text = markdown.Replace('\0', '�');
        if (extensions.HasFlag(MarkdownExtensions.FrontMatter))
        {
            text = parser.TakeFrontMatter(text);
        }

        int start = 0;
        while (start <= text.Length)
        {
            int end = start;
            while (end < text.Length && text[end] is not ('\n' or '\r'))
            {
                end++;
            }

            if (end == text.Length && start == text.Length)
            {
                break;
            }

            parser.IncorporateLine(text[start..end]);
            start = end + (end + 1 < text.Length && text[end] == '\r' && text[end + 1] == '\n' ? 2 : 1);
        }

        while (parser.tip is not null)
        {
            MdBlock? parent = parser.tip.Parent as MdBlock;
            parser.Finalize(parser.tip);
            if (parent is null)
            {
                break;
            }

            parser.tip = parent;
        }

        InlineParser.ParseAll(parser.document, extensions);
        return parser.document;
    }

    private string TakeFrontMatter(string text)
    {
        Match match = FrontMatterRegex().Match(text);
        if (!match.Success)
        {
            return text;
        }

        document.FrontMatter = match.Groups[1].Value;
        return text[match.Length..];
    }

    private void IncorporateLine(string ln)
    {
        MdBlock container = document;
        oldTip = tip;
        offset = 0;
        column = 0;
        blank = false;
        partiallyConsumedTab = false;
        lineNumber++;
        line = ln;

        // 1. Go through the open blocks; each one the line continues stays matched.
        while (container.LastChild is MdBlock { Open: true } last)
        {
            container = last;
            FindNextNonspace();
            switch (Continue(container))
            {
                case 0:
                    break;
                case 1:
                    container = (MdBlock)container.Parent!;
                    goto Matched;
                case 2:
                    return;
            }
        }

    Matched:
        allClosed = container == oldTip;
        lastMatchedContainer = container;
        bool matchedLeaf = container is not (MdParagraph or MdTable) && container.AcceptsLines;

        // 2. Look for new block starts, until a leaf block starts.
        while (!matchedLeaf)
        {
            FindNextNonspace();
            if (!indented && !MaybeSpecial(line, nextNonspace))
            {
                AdvanceNextNonspace();
                break;
            }

            int result = TryStarts(container);
            if (result == 0)
            {
                AdvanceNextNonspace();
                break;
            }

            container = tip;
            if (result == 2)
            {
                matchedLeaf = true;
            }

            if (lineConsumed)
            {
                lineConsumed = false;
                return;
            }
        }

        // 3. What is left of the line: a lazy paragraph continuation, or text for the deepest block.
        if (!allClosed && !blank && tip is MdParagraph)
        {
            AddLine();
            return;
        }

        CloseUnmatchedBlocks();
        if (blank && container.LastChild is MdBlock lastChild)
        {
            lastChild.LastLineBlank = true;
        }

        bool lastLineBlank = blank && !(container is MdBlockQuote
            || (container is MdCodeBlock { Fenced: true })
            || (container is MdListItem && container.Children.Count == 0 && container.StartLine == lineNumber));
        for (MdBlock? c = container; c is not null; c = c.Parent as MdBlock)
        {
            c.LastLineBlank = lastLineBlank;
        }

        if (container.AcceptsLines)
        {
            AddLine();
            if (container is MdHtmlBlock html && html.HtmlType is >= 1 and <= 5 && HtmlBlockEnds(html.HtmlType, line[offset..]))
            {
                Finalize(container);
            }
        }
        else if (offset < line.Length && !blank)
        {
            AddChild(new MdParagraph());
            AdvanceNextNonspace();
            AddLine();
        }
    }

    /// <summary>Whether the line could start a block at <paramref name="at"/> without being indented.</summary>
    private static bool MaybeSpecial(string text, int at)
    {
        if (at >= text.Length)
        {
            return false;
        }

        char c = text[at];
        return c is '#' or '`' or '~' or '*' or '+' or '_' or '=' or '<' or '>' or '-' or '|' or ':' or '[' || char.IsAsciiDigit(c);
    }

    /// <summary>0 when the line continues the block, 1 when it does not, 2 when it ends it (a closing fence).</summary>
    private int Continue(MdBlock block)
    {
        switch (block)
        {
            case MdDocument or MdList:
                return 0;
            case MdBlockQuote:
                if (!indented && Peek(nextNonspace) == '>')
                {
                    AdvanceNextNonspace();
                    AdvanceOffset(1, false);
                    if (Peek(offset) is ' ' or '\t')
                    {
                        AdvanceOffset(1, true);
                    }

                    return 0;
                }

                return 1;
            case MdListItem item:
                if (blank)
                {
                    if (item.Children.Count == 0)
                    {
                        return 1;
                    }

                    AdvanceNextNonspace();
                    return 0;
                }

                if (indent >= item.MarkerOffset + item.Padding)
                {
                    AdvanceOffset(item.MarkerOffset + item.Padding, true);
                    return 0;
                }

                return 1;
            case MdFootnoteDefinition note:
                if (blank)
                {
                    AdvanceNextNonspace();
                    return 0;
                }

                if (indent >= CodeIndent)
                {
                    AdvanceOffset(CodeIndent, true);
                    return 0;
                }

                return 1;
            case MdCodeBlock { Fenced: true } fence:
                Match closing = ClosingFenceRegex().Match(line, nextNonspace);
                if (indent <= 3 && Peek(nextNonspace) == fence.FenceChar && closing.Success && closing.Index == nextNonspace
                    && closing.Groups[1].Length >= fence.FenceLength && closing.Groups[1].Value[0] == fence.FenceChar)
                {
                    Finalize(fence);
                    return 2;
                }

                for (int i = fence.FenceOffset; i > 0 && Peek(offset) is ' ' or '\t'; i--)
                {
                    AdvanceOffset(1, true);
                }

                return 0;
            case MdCodeBlock:
                if (indent >= CodeIndent)
                {
                    AdvanceOffset(CodeIndent, true);
                }
                else if (blank)
                {
                    AdvanceNextNonspace();
                }
                else
                {
                    return 1;
                }

                return 0;
            case MdHtmlBlock html:
                return blank && html.HtmlType is 6 or 7 ? 1 : 0;
            case MdParagraph or MdTable:
                return blank ? 1 : 0;
            default:
                return 1;
        }
    }

    /// <summary>Tries each block start in the spec's order: 0 none, 1 a container started, 2 a leaf started.</summary>
    private int TryStarts(MdBlock container)
    {
        string rest = line[nextNonspace..];
        char c = Peek(nextNonspace);

        // Containers deeper than the limit (thousands of '>' in hostile input) start no more containers: their markers stay text.
        bool deep = Depth(container) >= InlineParser.MaxDepth;

        // Block quote.
        if (!indented && c == '>' && !deep)
        {
            AdvanceNextNonspace();
            AdvanceOffset(1, false);
            if (Peek(offset) is ' ' or '\t')
            {
                AdvanceOffset(1, true);
            }

            CloseUnmatchedBlocks();
            AddChild(new MdBlockQuote());
            return 1;
        }

        // ATX heading.
        Match match;
        if (!indented && c == '#' && (match = AtxRegex().Match(rest)).Success)
        {
            AdvanceNextNonspace();
            AdvanceOffset(match.Length, false);
            CloseUnmatchedBlocks();
            MdHeading heading = AddChild(new MdHeading { Level = match.Groups[1].Length });
            string text = line[offset..];
            text = AtxClosingRegex().Replace(AtxClosingOnlyRegex().Replace(text, ""), "");
            heading.Content.Append(text.Trim(' ', '\t'));
            AdvanceOffset(line.Length - offset, false);
            return 2;
        }

        // Fenced code.
        if (!indented && c is '`' or '~' && (match = OpeningFenceRegex().Match(rest)).Success)
        {
            CloseUnmatchedBlocks();
            AddChild(new MdCodeBlock { Fenced = true, FenceChar = c, FenceLength = match.Length, FenceOffset = indent });
            AdvanceNextNonspace();
            AdvanceOffset(match.Length, false);
            return 2;
        }

        // HTML block.
        if (!indented && c == '<')
        {
            for (int type = 1; type <= 7; type++)
            {
                if (HtmlBlockStarts(type, rest) && (type < 7 || (container is not MdParagraph && !(!allClosed && !blank && tip is MdParagraph))))
                {
                    CloseUnmatchedBlocks();
                    AddChild(new MdHtmlBlock { HtmlType = type });
                    return 2;
                }
            }
        }

        // GitHub table: a delimiter row under a paragraph whose last line is a header row with as many cells.
        if (extensions.HasFlag(MarkdownExtensions.Tables) && !indented && container is MdParagraph paragraph && TryTable(paragraph, rest))
        {
            return 2;
        }

        // Setext heading.
        if (!indented && container is MdParagraph para && (match = SetextRegex().Match(rest)).Success)
        {
            CloseUnmatchedBlocks();
            ParseReferences(para);
            string content = para.Content.ToString();
            if (content.Trim().Length > 0)
            {
                MdHeading heading = new() { Level = match.Groups[1].Value[0] == '=' ? 1 : 2, Setext = true, Open = true };
                heading.Content.Append(content.TrimEnd('\n'));
                ((MdBlock)para.Parent!).InsertAfter(para, heading);
                para.Remove();
                tip = heading;
                AdvanceOffset(line.Length - offset, false);
                return 2;
            }

            return 0;
        }

        // Thematic break.
        if (!indented && ThematicBreakRegex().IsMatch(rest))
        {
            CloseUnmatchedBlocks();
            AddChild(new MdThematicBreak());
            AdvanceOffset(line.Length - offset, false);
            return 2;
        }

        // GitHub footnote definition.
        if (extensions.HasFlag(MarkdownExtensions.Footnotes) && !indented && c == '[' && !deep && (match = FootnoteDefinitionRegex().Match(rest)).Success)
        {
            CloseUnmatchedBlocks();
            string label = InlineParser.NormalizeLabel(match.Groups[1].Value);
            MdFootnoteDefinition definition = AddChild(new MdFootnoteDefinition { Label = label });
            document.Footnotes.TryAdd(label, definition);
            AdvanceNextNonspace();
            AdvanceOffset(match.Length, false);
            while (Peek(offset) is ' ' or '\t')
            {
                AdvanceOffset(1, true);
            }

            return 1;
        }

        // List item.
        if ((!indented || container is MdList) && !deep && ParseListMarker(container) is { } item)
        {
            CloseUnmatchedBlocks();
            if (tip is not MdList list || !ListsMatch(list, item))
            {
                AddChild(new MdList { Ordered = item.Ordered, Start = item.Start, BulletChar = item.BulletChar, Delimiter = item.Delimiter });
            }

            AddChild(item);
            return 1;
        }

        // Indented code.
        if (indented && tip is not MdParagraph && !blank)
        {
            AdvanceOffset(CodeIndent, true);
            CloseUnmatchedBlocks();
            AddChild(new MdCodeBlock());
            return 2;
        }

        return 0;
    }

    private static int Depth(MdNode node)
    {
        int depth = 0;
        for (MdNode? n = node; n is not null; n = n.Parent)
        {
            depth++;
        }

        return depth;
    }

    private bool TryTable(MdParagraph paragraph, string rest)
    {
        if (!rest.Contains('|') || !DelimiterRowRegex().IsMatch(rest))
        {
            return false;
        }

        string content = paragraph.Content.ToString().TrimEnd('\n');
        int lastBreak = content.LastIndexOf('\n');
        string header = content[(lastBreak + 1)..];
        List<string> headerCells = SplitCells(header);
        List<string> delimiters = SplitCells(rest);
        if (headerCells.Count != delimiters.Count || headerCells.Count == 0)
        {
            return false;
        }

        CloseUnmatchedBlocks();
        MdTable table = new();
        foreach (string d in delimiters)
        {
            string cell = d.Trim();
            table.Alignments.Add(cell.StartsWith(':') ? (cell.EndsWith(':') ? MdAlign.Center : MdAlign.Left) : cell.EndsWith(':') ? MdAlign.Right : MdAlign.None);
        }

        MdTableRow headerRow = new() { Header = true, Open = false };
        for (int i = 0; i < headerCells.Count; i++)
        {
            MdTableCell cell = new() { Align = table.Alignments[i], Open = false };
            cell.Content.Append(headerCells[i]);
            headerRow.Append(cell);
        }

        table.Append(headerRow);
        MdBlock parent = (MdBlock)paragraph.Parent!;
        if (lastBreak < 0)
        {
            parent.InsertAfter(paragraph, table);
            paragraph.Remove();
        }
        else
        {
            paragraph.Content.Clear().Append(content[..lastBreak]).Append('\n');
            Finalize(paragraph);
            parent.InsertAfter(paragraph, table);
        }

        tip = table;
        AdvanceOffset(line.Length - offset, false);
        lineConsumed = true;
        return true;
    }

    /// <summary>A table line's cells: split at pipes that are not escaped or inside code spans, leading and trailing pipe dropped.</summary>
    internal static List<string> SplitCells(string row)
    {
        string text = row.Trim(' ', '\t');
        List<string> cells = [];
        StringBuilder cell = new();
        int start = text.StartsWith('|') ? 1 : 0;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '|')
            {
                cell.Append('|');
                i++;
            }
            else if (c == '|')
            {
                cells.Add(cell.ToString().Trim(' ', '\t'));
                cell.Clear();
                if (i == text.Length - 1)
                {
                    return cells;
                }
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString().Trim(' ', '\t'));
        return cells;
    }

    private MdListItem? ParseListMarker(MdBlock container)
    {
        if (indent >= CodeIndent)
        {
            return null;
        }

        string rest = line[nextNonspace..];
        MdListItem item = new() { MarkerOffset = indent };
        int markerLength;
        Match match;
        if ((match = BulletRegex().Match(rest)).Success)
        {
            item.BulletChar = match.Value[0];
            markerLength = 1;
        }
        else if ((match = OrderedRegex().Match(rest)).Success && (container is not MdParagraph || match.Groups[1].Value == "1"))
        {
            item.Ordered = true;
            item.Start = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            item.Delimiter = match.Groups[2].Value[0];
            markerLength = match.Length;
        }
        else
        {
            return null;
        }

        char next = Peek(nextNonspace + markerLength);
        if (next is not ('\0' or ' ' or '\t'))
        {
            return null;
        }

        // A list item that would be empty cannot interrupt a paragraph.
        if (container is MdParagraph && line[(nextNonspace + markerLength)..].Trim(' ', '\t').Length == 0)
        {
            return null;
        }

        AdvanceNextNonspace();
        AdvanceOffset(markerLength, true);
        int spacesStartColumn = column;
        int spacesStartOffset = offset;
        do
        {
            AdvanceOffset(1, true);
            next = Peek(offset);
        }
        while (column - spacesStartColumn < 5 && next is ' ' or '\t');

        bool blankItem = Peek(offset) == '\0';
        int spacesAfterMarker = column - spacesStartColumn;
        if (spacesAfterMarker >= 5 || spacesAfterMarker < 1 || blankItem)
        {
            item.Padding = markerLength + 1;
            column = spacesStartColumn;
            offset = spacesStartOffset;
            if (Peek(offset) is ' ' or '\t')
            {
                AdvanceOffset(1, true);
            }
        }
        else
        {
            item.Padding = markerLength + spacesAfterMarker;
        }

        return item;
    }

    private static bool ListsMatch(MdList list, MdListItem item)
    {
        return list.Ordered == item.Ordered && list.Delimiter == item.Delimiter && list.BulletChar == item.BulletChar;
    }

    private T AddChild<T>(T block)
        where T : MdBlock
    {
        while (!tip.CanContain(block))
        {
            Finalize(tip);
        }

        tip.Append(block);
        block.StartLine = lineNumber;
        tip = block;
        return block;
    }

    private void CloseUnmatchedBlocks()
    {
        if (allClosed)
        {
            return;
        }

        while (oldTip != lastMatchedContainer)
        {
            MdBlock parent = (MdBlock)oldTip.Parent!;
            Finalize(oldTip);
            oldTip = parent;
        }

        allClosed = true;
    }

    private void AddLine()
    {
        if (partiallyConsumedTab)
        {
            offset++;
            int charsToTab = 4 - (column % 4);
            tip.Content.Append(' ', charsToTab);
        }

        if (tip is MdTable table)
        {
            AddTableRow(table, line[offset..]);
            return;
        }

        tip.Content.Append(line, offset, line.Length - offset).Append('\n');
    }

    private static void AddTableRow(MdTable table, string text)
    {
        List<string> cells = SplitCells(text);
        MdTableRow row = new() { Open = false };
        for (int i = 0; i < table.Alignments.Count; i++)
        {
            MdTableCell cell = new() { Align = table.Alignments[i], Open = false };
            cell.Content.Append(i < cells.Count ? cells[i] : "");
            row.Append(cell);
        }

        table.Append(row);
    }

    private void Finalize(MdBlock block)
    {
        MdBlock? above = block.Parent as MdBlock;
        block.Open = false;
        switch (block)
        {
            case MdParagraph paragraph:
                ParseReferences(paragraph);
                if (paragraph.Content.ToString().Trim(' ', '\t', '\n').Length == 0)
                {
                    paragraph.Remove();
                }

                break;
            case MdCodeBlock { Fenced: true } fence:
                {
                    string content = fence.Content.ToString();
                    int newline = content.IndexOf('\n');
                    string infoLine = newline < 0 ? content : content[..newline];
                    fence.Info = InlineParser.UnescapeString(infoLine.Trim(' ', '\t'));
                    fence.Literal = newline < 0 ? "" : content[(newline + 1)..];
                    break;
                }

            case MdCodeBlock code:
                {
                    string content = code.Content.ToString();
                    code.Literal = TrailingBlankLinesRegex().Replace(content, "\n");
                    if (code.Literal == "\n")
                    {
                        code.Literal = "";
                    }

                    break;
                }

            case MdHtmlBlock html:
                html.Literal = HtmlTrailingRegex().Replace(html.Content.ToString(), "");
                break;
            case MdList list:
                list.Tight = IsTight(list);
                break;
            case MdListItem item when extensions.HasFlag(MarkdownExtensions.TaskLists):
                if (item.Children.Count > 0 && item.Children[0] is MdParagraph first && TaskRegex().Match(first.Content.ToString()) is { Success: true } task)
                {
                    item.TaskChecked = task.Groups[1].Value != " ";
                    first.Content.Remove(0, task.Length);
                }

                break;
        }

        tip = above ?? document;
    }

    private static bool IsTight(MdList list)
    {
        for (int i = 0; i < list.Children.Count; i++)
        {
            MdBlock item = (MdBlock)list.Children[i];
            bool hasNextItem = i + 1 < list.Children.Count;
            if (EndsWithBlankLine(item) && hasNextItem)
            {
                return false;
            }

            for (int j = 0; j < item.Children.Count; j++)
            {
                if (EndsWithBlankLine((MdBlock)item.Children[j]) && (hasNextItem || j + 1 < item.Children.Count))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool EndsWithBlankLine(MdBlock block)
    {
        MdBlock? b = block;
        while (b is not null)
        {
            if (b.LastLineBlank)
            {
                return true;
            }

            b = b is MdList or MdListItem ? b.LastChild as MdBlock : null;
        }

        return false;
    }

    /// <summary>Takes link reference definitions off the start of a paragraph into the document's map.</summary>
    private void ParseReferences(MdParagraph paragraph)
    {
        string content = paragraph.Content.ToString();
        int consumed = 0;
        while (consumed < content.Length && content[consumed] == '[')
        {
            int length = InlineParser.ParseReference(content, consumed, document.References);
            if (length == 0)
            {
                break;
            }

            consumed += length;
        }

        if (consumed > 0)
        {
            paragraph.Content.Remove(0, consumed);
        }
    }

    private char Peek(int at)
    {
        return at < line.Length ? line[at] : '\0';
    }

    private void FindNextNonspace()
    {
        int i = offset;
        int cols = column;
        char c;
        while ((c = Peek(i)) != '\0')
        {
            if (c == ' ')
            {
                i++;
                cols++;
            }
            else if (c == '\t')
            {
                i++;
                cols += 4 - (cols % 4);
            }
            else
            {
                break;
            }
        }

        blank = c == '\0';
        nextNonspace = i;
        nextNonspaceColumn = cols;
        indent = nextNonspaceColumn - column;
        indented = indent >= CodeIndent;
    }

    private void AdvanceNextNonspace()
    {
        offset = nextNonspace;
        column = nextNonspaceColumn;
        partiallyConsumedTab = false;
    }

    private void AdvanceOffset(int count, bool columns)
    {
        while (count > 0 && offset < line.Length)
        {
            char c = line[offset];
            if (c == '\t')
            {
                int charsToTab = 4 - (column % 4);
                if (columns)
                {
                    partiallyConsumedTab = charsToTab > count;
                    int charsToAdvance = Math.Min(charsToTab, count);
                    column += charsToAdvance;
                    offset += partiallyConsumedTab ? 0 : 1;
                    count -= charsToAdvance;
                }
                else
                {
                    partiallyConsumedTab = false;
                    column += charsToTab;
                    offset++;
                    count--;
                }
            }
            else
            {
                partiallyConsumedTab = false;
                offset++;
                column++;
                count--;
            }
        }
    }

    private static readonly string[] BlockTagNames =
    [
        "address", "article", "aside", "base", "basefont", "blockquote", "body", "caption", "center", "col", "colgroup", "dd", "details",
        "dialog", "dir", "div", "dl", "dt", "fieldset", "figcaption", "figure", "footer", "form", "frame", "frameset", "h1", "h2", "h3",
        "h4", "h5", "h6", "head", "header", "hr", "html", "iframe", "legend", "li", "link", "main", "menu", "menuitem", "nav", "noframes",
        "ol", "optgroup", "option", "p", "param", "search", "section", "summary", "table", "tbody", "td", "tfoot", "th", "thead", "title",
        "tr", "track", "ul",
    ];

    private static bool HtmlBlockStarts(int type, string rest)
    {
        switch (type)
        {
            case 1:
                return HtmlStart1Regex().IsMatch(rest);
            case 2:
                return rest.StartsWith("<!--", StringComparison.Ordinal);
            case 3:
                return rest.StartsWith("<?", StringComparison.Ordinal);
            case 4:
                return rest.Length > 2 && rest[1] == '!' && char.IsAsciiLetter(rest[2]);
            case 5:
                return rest.StartsWith("<![CDATA[", StringComparison.Ordinal);
            case 6:
                Match m = HtmlStart6Regex().Match(rest);
                return m.Success && BlockTagNames.Contains(m.Groups[1].Value.ToLowerInvariant());
            default:
                return HtmlStart7Regex().IsMatch(rest);
        }
    }

    private static bool HtmlBlockEnds(int type, string text)
    {
        return type switch
        {
            1 => HtmlEnd1Regex().IsMatch(text),
            2 => text.Contains("-->", StringComparison.Ordinal),
            3 => text.Contains("?>", StringComparison.Ordinal),
            4 => text.Contains('>'),
            5 => text.Contains("]]>", StringComparison.Ordinal),
            _ => false,
        };
    }

    [GeneratedRegex(@"\A---[ \t]*\r?\n([\s\S]*?)\r?\n(?:---|\.\.\.)[ \t]*(?:\r?\n|\z)")]
    private static partial Regex FrontMatterRegex();

    [GeneratedRegex(@"^(#{1,6})(?:[ \t]+|$)")]
    private static partial Regex AtxRegex();

    [GeneratedRegex(@"^[ \t]*#+[ \t]*$")]
    private static partial Regex AtxClosingOnlyRegex();

    [GeneratedRegex(@"[ \t]+#+[ \t]*$")]
    private static partial Regex AtxClosingRegex();

    [GeneratedRegex(@"^(?:`{3,}(?!.*`)|~{3,})")]
    private static partial Regex OpeningFenceRegex();

    [GeneratedRegex(@"\G(`{3,}|~{3,})(?=[ \t]*$)")]
    private static partial Regex ClosingFenceRegex();

    [GeneratedRegex(@"^(=+|-+)[ \t]*$")]
    private static partial Regex SetextRegex();

    [GeneratedRegex(@"^(?:\*[ \t]*){3,}$|^(?:_[ \t]*){3,}$|^(?:-[ \t]*){3,}$")]
    private static partial Regex ThematicBreakRegex();

    [GeneratedRegex(@"^[*+-]")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^(\d{1,9})([.)])")]
    private static partial Regex OrderedRegex();

    [GeneratedRegex(@"^\[\^([^\]\s]+)\]:")]
    private static partial Regex FootnoteDefinitionRegex();

    [GeneratedRegex(@"^\|?[ \t]*:?-+:?[ \t]*(\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$")]
    private static partial Regex DelimiterRowRegex();

    [GeneratedRegex(@"^\[([ xX])\](?=[ \t]|\n|$)[ \t]?")]
    private static partial Regex TaskRegex();

    [GeneratedRegex(@"(?:\n[ \t]*)+$")]
    private static partial Regex HtmlTrailingRegex();

    [GeneratedRegex(@"(?:\n[ \t]*)+\z")]
    private static partial Regex TrailingBlankLinesRegex();

    [GeneratedRegex(@"^<(?:script|pre|textarea|style)(?:\s|>|$)", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlStart1Regex();

    [GeneratedRegex(@"</(?:script|pre|textarea|style)>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlEnd1Regex();

    [GeneratedRegex(@"^</?([a-zA-Z][a-zA-Z0-9-]*)(?:\s|/?>|$)")]
    private static partial Regex HtmlStart6Regex();

    [GeneratedRegex(@"^(?:<[A-Za-z][A-Za-z0-9-]*(?:\s+[a-zA-Z_:][a-zA-Z0-9_.:-]*(?:\s*=\s*(?:[^""'=<>`\x00-\x20]+|'[^']*'|""[^""]*""))?)*\s*/?>|</[A-Za-z][A-Za-z0-9-]*\s*>)\s*$")]
    private static partial Regex HtmlStart7Regex();
}
