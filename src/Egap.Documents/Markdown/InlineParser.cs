using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Egap.Documents.Markdown;

/// <summary>
/// The inline phase of CommonMark parsing (spec 0.31, appendix "Phase 2: inline structure"): code spans, the delimiter run
/// algorithm for emphasis, links and images with the bracket stack, autolinks, raw HTML, entities, escapes and line breaks,
/// plus GitHub's strikethrough, extended autolinks and footnote references. Written from the spec.
/// </summary>
internal sealed partial class InlineParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How deep inlines may nest; deeper ones are flattened.</summary>
    internal const int MaxDepth = 64;

    /// <summary>For each backtick run length, a position from which no closing run of that length exists (cmark's cache).</summary>
    private readonly Dictionary<int, int> noCloserAfter = [];

    private readonly Dictionary<string, (string Destination, string? Title)> references;
    private readonly MdDocument document;
    private readonly MarkdownExtensions extensions;
    private string subject = "";
    private int pos;
    private Delimiter? delimiters;
    private Bracket? brackets;
    private MdNode block = null!;

    private InlineParser(MdDocument document, MarkdownExtensions extensions)
    {
        this.document = document;
        references = document.References;
        this.extensions = extensions;
    }

    /// <summary>Parses the inlines of every paragraph, heading and table cell, numbering footnotes by first reference.</summary>
    public static void ParseAll(MdDocument document, MarkdownExtensions extensions)
    {
        InlineParser parser = new(document, extensions);
        Stack<MdNode> pending = new();
        pending.Push(document);
        List<MdBlock> leaves = [];
        while (pending.Count > 0)
        {
            MdNode node = pending.Pop();
            if (node is MdParagraph or MdHeading or MdTableCell)
            {
                leaves.Add((MdBlock)node);
            }

            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                pending.Push(node.Children[i]);
            }
        }

        foreach (MdBlock leaf in leaves)
        {
            parser.Parse(leaf);
        }
    }

    /// <summary>Normalizes a link label: case folded, inner white space collapsed to one space, trimmed.</summary>
    public static string NormalizeLabel(string label)
    {
        string trimmed = label.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length >= 2)
        {
            trimmed = trimmed[1..^1];
        }

        string folded = trimmed.ToUpperInvariant().ToLowerInvariant().Replace("ß", "ss", StringComparison.Ordinal);
        return WhitespaceRunRegex().Replace(folded.Trim(), " ");
    }

    /// <summary>Removes backslash escapes and decodes entities in a link destination, title or info string.</summary>
    public static string UnescapeString(string text)
    {
        if (text.IndexOfAny(['\\', '&']) < 0)
        {
            return text;
        }

        StringBuilder result = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && IsAsciiPunctuation(text[i + 1]))
            {
                result.Append(text[++i]);
            }
            else if (c == '&' && EntityRegex().Match(text, i) is { Success: true } m && m.Index == i)
            {
                result.Append(DecodeEntity(m.Value));
                i += m.Length - 1;
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Parses a link reference definition at <paramref name="start"/> of <paramref name="text"/> into
    /// <paramref name="map"/> (the first definition of a label wins); returns the characters it took, or 0.
    /// </summary>
    public static int ParseReference(string text, int start, Dictionary<string, (string, string?)> map)
    {
        InlineParser parser = new(new MdDocument(), MarkdownExtensions.None) { subject = text, pos = start };
        return parser.ParseReferenceAt(map);
    }

    private void Parse(MdBlock leaf)
    {
        block = leaf;
        subject = leaf.Content.ToString().Trim(' ', '\t', '\n');
        pos = 0;
        delimiters = null;
        brackets = null;
        while (pos < subject.Length)
        {
            if (!ParseInline())
            {
                pos++;
                AppendText(subject[(pos - 1)..pos]);
            }
        }

        ProcessEmphasis(null);
        leaf.Materialize();
        LimitDepth(leaf);
        MergeText(leaf);
        if (extensions.HasFlag(MarkdownExtensions.Autolinks))
        {
            ExtendedAutolinks(leaf);
            MergeText(leaf);
        }
    }

    private bool ParseInline()
    {
        char c = subject[pos];
        switch (c)
        {
            case '\n':
                return ParseNewline();
            case '\\':
                return ParseBackslash();
            case '`':
                return ParseBackticks();
            case '*' or '_':
                return HandleDelimiter(c);
            case '~' when extensions.HasFlag(MarkdownExtensions.Strikethrough):
                return HandleDelimiter(c);
            case '[':
                pos++;
                AddBracket(AppendText("["), pos - 1, image: false);
                return true;
            case '!':
                pos++;
                if (Peek() == '[')
                {
                    pos++;
                    AddBracket(AppendText("!["), pos - 1, image: true);
                }
                else
                {
                    AppendText("!");
                }

                return true;
            case ']':
                return ParseCloseBracket();
            case '<':
                return ParseAutolink() || ParseHtmlTag();
            case '&':
                return ParseEntity();
            default:
                return ParseString();
        }
    }

    private bool Starts(string text)
    {
        return string.CompareOrdinal(subject, pos, text, 0, text.Length) == 0;
    }

    private char Peek(int ahead = 0)
    {
        return pos + ahead < subject.Length ? subject[pos + ahead] : '\0';
    }

    private MdText AppendText(string text)
    {
        MdText node = new(text);
        block.LinkAppend(node);
        return node;
    }

    private bool ParseString()
    {
        int start = pos;
        while (pos < subject.Length && !IsSpecial(subject[pos]))
        {
            pos++;
        }

        if (pos == start)
        {
            return false;
        }

        AppendText(subject[start..pos]);
        return true;
    }

    private bool IsSpecial(char c)
    {
        return c is '\n' or '\\' or '`' or '*' or '_' or '[' or ']' or '!' or '<' or '&' || (c == '~' && extensions.HasFlag(MarkdownExtensions.Strikethrough));
    }

    private bool ParseNewline()
    {
        pos++;
        if (block.LinkLast is MdText last && last.Literal.EndsWith(' '))
        {
            bool hard = last.Literal.EndsWith("  ", StringComparison.Ordinal);
            last.Literal = last.Literal.TrimEnd(' ');
            if (last.Literal.Length == 0)
            {
                last.LinkUnlink();
            }

            block.LinkAppend(hard ? new MdHardBreak() : new MdSoftBreak());
        }
        else
        {
            block.LinkAppend(new MdSoftBreak());
        }

        while (Peek() is ' ' or '\t')
        {
            pos++;
        }

        return true;
    }

    private bool ParseBackslash()
    {
        pos++;
        if (Peek() == '\n')
        {
            pos++;
            block.LinkAppend(new MdHardBreak());
            while (Peek() is ' ' or '\t')
            {
                pos++;
            }
        }
        else if (pos < subject.Length && IsAsciiPunctuation(subject[pos]))
        {
            AppendText(subject[pos].ToString());
            pos++;
        }
        else
        {
            AppendText("\\");
        }

        return true;
    }

    private bool ParseBackticks()
    {
        int start = pos;
        while (Peek() == '`')
        {
            pos++;
        }

        int length = pos - start;
        int after = pos;
        int search = pos;
        if (noCloserAfter.TryGetValue(length, out int none) && none <= pos)
        {
            AppendText(subject[start..after]);
            return true;
        }

        while (search < subject.Length)
        {
            int open = subject.IndexOf('`', search);
            if (open < 0)
            {
                break;
            }

            int close = open;
            while (close < subject.Length && subject[close] == '`')
            {
                close++;
            }

            if (close - open == length)
            {
                string content = subject[after..open].Replace('\n', ' ');
                if (content.Length >= 2 && content[0] == ' ' && content[^1] == ' ' && content.Trim(' ').Length > 0)
                {
                    content = content[1..^1];
                }

                block.LinkAppend(new MdCode(content));
                pos = close;
                return true;
            }

            search = close;
        }

        noCloserAfter[length] = after;
        AppendText(subject[start..after]);
        return true;
    }

    private bool ParseAutolink()
    {
        Match m;
        if ((m = EmailAutolinkRegex().Match(subject, pos)).Success && m.Index == pos)
        {
            string address = m.Value[1..^1];
            MdLink link = new("mailto:" + address, null) { Auto = true };
            link.Append(new MdText(address));
            block.LinkAppend(link);
            pos += m.Length;
            return true;
        }

        if ((m = UriAutolinkRegex().Match(subject, pos)).Success && m.Index == pos)
        {
            string address = m.Value[1..^1];
            MdLink link = new(address, null) { Auto = true };
            link.Append(new MdText(address));
            block.LinkAppend(link);
            pos += m.Length;
            return true;
        }

        return false;
    }

    private bool ParseHtmlTag()
    {
        // Comments, processing instructions and CDATA that never close would make the expression scan to the end each time.
        if ((Starts("<!--") && !Starts("<!-->") && !Starts("<!--->") && subject.IndexOf("-->", pos + 4, StringComparison.Ordinal) < 0)
            || (Starts("<?") && subject.IndexOf("?>", pos + 2, StringComparison.Ordinal) < 0)
            || (Starts("<![CDATA[") && subject.IndexOf("]]>", pos + 9, StringComparison.Ordinal) < 0))
        {
            return false;
        }

        Match m = HtmlTagRegex().Match(subject, pos);
        if (!m.Success || m.Index != pos)
        {
            return false;
        }

        block.LinkAppend(new MdHtmlInline(m.Value));
        pos += m.Length;
        return true;
    }

    private bool ParseEntity()
    {
        Match m = EntityRegex().Match(subject, pos);
        if (!m.Success || m.Index != pos)
        {
            return false;
        }

        string decoded = DecodeEntity(m.Value);
        if (decoded == m.Value)
        {
            return false;
        }

        AppendText(decoded);
        pos += m.Length;
        return true;
    }

    private static string DecodeEntity(string entity)
    {
        if (entity.StartsWith("&#", StringComparison.Ordinal))
        {
            bool hex = entity[2] is 'x' or 'X';
            string digits = entity[(hex ? 3 : 2)..^1];
            long code = long.Parse(digits, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture);
            return HtmlEntities.FromCodePoint(code, windows1252: false);
        }

        return HtmlEntities.Lookup(entity.AsSpan(1)) ?? entity;
    }

    // Delimiter runs (emphasis and strikethrough).
    private bool HandleDelimiter(char c)
    {
        int start = pos;
        while (Peek() == c)
        {
            pos++;
        }

        int count = pos - start;
        if (c == '~' && count > 2)
        {
            AppendText(subject[start..pos]);
            return true;
        }

        int before = start == 0 ? '\n' : CodePointAt(subject, start - 1, back: true);
        int after = pos >= subject.Length ? '\n' : CodePointAt(subject, pos, back: false);
        bool afterSpace = IsUnicodeWhitespace(after);
        bool afterPunct = IsUnicodePunctuation(after);
        bool beforeSpace = IsUnicodeWhitespace(before);
        bool beforePunct = IsUnicodePunctuation(before);
        bool leftFlanking = !afterSpace && (!afterPunct || beforeSpace || beforePunct);
        bool rightFlanking = !beforeSpace && (!beforePunct || afterSpace || afterPunct);
        bool canOpen, canClose;
        if (c == '_')
        {
            canOpen = leftFlanking && (!rightFlanking || beforePunct);
            canClose = rightFlanking && (!leftFlanking || afterPunct);
        }
        else
        {
            canOpen = leftFlanking;
            canClose = rightFlanking;
        }

        MdText node = AppendText(subject[start..pos]);
        if (canOpen || canClose)
        {
            Delimiter delimiter = new(c, count, node, canOpen, canClose) { Previous = delimiters };
            if (delimiters is not null)
            {
                delimiters.Next = delimiter;
            }

            delimiters = delimiter;
        }

        return true;
    }

    private void RemoveDelimiter(Delimiter delimiter)
    {
        if (delimiter.Previous is not null)
        {
            delimiter.Previous.Next = delimiter.Next;
        }

        if (delimiter.Next is null)
        {
            delimiters = delimiter.Previous;
        }
        else
        {
            delimiter.Next.Previous = delimiter.Previous;
        }
    }

    private void ProcessEmphasis(Delimiter? stackBottom)
    {
        Dictionary<(char, int), Delimiter?> openersBottom = [];
        Delimiter? closer = delimiters;
        while (closer is not null && closer.Previous != stackBottom)
        {
            closer = closer.Previous;
        }

        while (closer is not null)
        {
            if (!closer.CanClose)
            {
                closer = closer.Next;
                continue;
            }

            char cc = closer.Char;
            int bottomIndex = cc == '~' ? closer.Count : (closer.CanOpen ? 3 : 0) + (closer.OriginalCount % 3);
            Delimiter? opener = closer.Previous;
            bool found = false;
            Delimiter? bottom = openersBottom.GetValueOrDefault((cc, bottomIndex));
            while (opener is not null && opener != stackBottom && opener != bottom)
            {
                bool oddMatch = cc != '~' && (closer.CanOpen || opener.CanClose) && closer.OriginalCount % 3 != 0
                    && (opener.OriginalCount + closer.OriginalCount) % 3 == 0;
                if (opener.Char == cc && opener.CanOpen && !oddMatch && (cc != '~' || opener.Count == closer.Count))
                {
                    found = true;
                    break;
                }

                opener = opener.Previous;
            }

            Delimiter oldCloser = closer;
            if (found)
            {
                int use = cc == '~' ? closer.Count : (closer.Count >= 2 && opener!.Count >= 2 ? 2 : 1);
                MdText openerNode = opener!.Node;
                MdText closerNode = closer.Node;
                opener.Count -= use;
                closer.Count -= use;
                openerNode.Literal = openerNode.Literal[..^use];
                closerNode.Literal = closerNode.Literal[..^use];
                MdNode wrapper = cc == '~' ? new MdStrikethrough() : use == 1 ? new MdEmphasis() : new MdStrong();
                MdNode.LinkMoveRange(openerNode, closerNode, wrapper);
                openerNode.LinkInsertAfter(wrapper);

                // Delimiters between the opener and the closer are done.
                for (Delimiter? d = closer.Previous; d is not null && d != opener;)
                {
                    Delimiter? previous = d.Previous;
                    RemoveDelimiter(d);
                    d = previous;
                }

                if (opener.Count == 0)
                {
                    openerNode.LinkUnlink();
                    RemoveDelimiter(opener);
                }

                if (closer.Count == 0)
                {
                    closerNode.LinkUnlink();
                    Delimiter? next = closer.Next;
                    RemoveDelimiter(closer);
                    closer = next;
                }
            }
            else
            {
                closer = closer.Next;
                openersBottom[(cc, bottomIndex)] = oldCloser.Previous;
                if (!oldCloser.CanOpen)
                {
                    RemoveDelimiter(oldCloser);
                }
            }
        }

        while (delimiters is not null && delimiters != stackBottom)
        {
            RemoveDelimiter(delimiters);
        }
    }

    // Links and images.
    private void AddBracket(MdText node, int index, bool image)
    {
        if (brackets is not null)
        {
            brackets.BracketAfter = true;
        }

        brackets = new Bracket(node, brackets, delimiters, index, image);
    }

    private bool ParseCloseBracket()
    {
        int startPos = pos;
        pos++;
        Bracket? opener = brackets;
        if (opener is null)
        {
            AppendText("]");
            return true;
        }

        if (!opener.Active)
        {
            AppendText("]");
            brackets = opener.Previous;
            return true;
        }

        string? destination = null;
        string? title = null;
        bool matched = false;
        int savePos = pos;

        // GitHub footnote reference: [^label] with a definition.
        if (extensions.HasFlag(MarkdownExtensions.Footnotes) && !opener.Image && startPos - opener.Index > 2 && startPos - opener.Index <= 1000 && subject[opener.Index + 1] == '^'
            && Peek() != '(' && document.Footnotes.TryGetValue(NormalizeLabel(subject[(opener.Index + 2)..startPos]), out MdFootnoteDefinition? definition))
        {
            if (definition.Number == 0)
            {
                definition.Number = document.Footnotes.Values.Count(d => d.Number > 0) + 1;
            }

            MdFootnoteReference reference = new(definition) { Ordinal = ++definition.ReferenceCount };
            ReplaceBracketWith(opener, reference, keepContents: false);
            return true;
        }

        // Inline link: [text](destination "title").
        if (Peek() == '(')
        {
            pos++;
            SkipSpaceAndNewline();
            if ((destination = ParseLinkDestination()) is not null)
            {
                int afterDestination = pos;
                SkipSpaceAndNewline();
                if (pos > afterDestination)
                {
                    title = ParseLinkTitle();
                }

                SkipSpaceAndNewline();
                if (Peek() == ')')
                {
                    pos++;
                    matched = true;
                }
            }

            if (!matched)
            {
                pos = savePos;
                destination = null;
                title = null;
            }
        }

        // Reference link: full, collapsed or shortcut.
        if (!matched)
        {
            int beforeLabel = pos;
            int n = ParseLinkLabel();
            string? label = null;
            if (n > 2)
            {
                label = subject.Substring(beforeLabel, n);
            }
            else if (!opener.BracketAfter && startPos - opener.Index <= 1000)
            {
                label = subject[opener.Index..(startPos + 1)];
            }

            if (n == 0)
            {
                pos = savePos;
            }

            if (label is not null && references.TryGetValue(NormalizeLabel(label), out (string Destination, string? Title) reference))
            {
                destination = reference.Destination;
                title = reference.Title;
                matched = true;
            }
        }

        if (!matched)
        {
            brackets = opener.Previous;
            pos = startPos + 1;
            AppendText("]");
            return true;
        }

        MdNode linkNode = opener.Image ? new MdImage(destination!, title) : new MdLink(destination!, title);
        ReplaceBracketWith(opener, linkNode, keepContents: true);
        if (!opener.Image)
        {
            for (Bracket? b = brackets; b is not null; b = b.Previous)
            {
                if (!b.Image)
                {
                    b.Active = false;
                }
            }
        }

        return true;
    }

    /// <summary>Moves what follows the opener into <paramref name="node"/> (or drops it), processes emphasis inside, removes the opener.</summary>
    private void ReplaceBracketWith(Bracket opener, MdNode node, bool keepContents)
    {
        if (keepContents)
        {
            MdNode.LinkMoveRange(opener.Node, null, node);
        }
        else
        {
            for (Delimiter? d = delimiters; d is not null && d != opener.PreviousDelimiter;)
            {
                Delimiter? previous = d.Previous;
                RemoveDelimiter(d);
                d = previous;
            }

            while (opener.Node.LinkNext is { } dropped)
            {
                dropped.LinkUnlink();
            }
        }

        opener.Node.Parent!.LinkAppend(node);
        if (keepContents)
        {
            ProcessEmphasis(opener.PreviousDelimiter);
        }

        brackets = opener.Previous;
        opener.Node.LinkUnlink();
    }

    private void SkipSpaceAndNewline()
    {
        while (Peek() is ' ' or '\t')
        {
            pos++;
        }

        if (Peek() == '\n')
        {
            pos++;
            while (Peek() is ' ' or '\t')
            {
                pos++;
            }
        }
    }

    private string? ParseLinkDestination()
    {
        if (Peek() == '<')
        {
            Match m = PointyDestinationRegex().Match(subject, pos);
            if (!m.Success || m.Index != pos)
            {
                return null;
            }

            pos += m.Length;
            return UnescapeString(m.Value[1..^1]);
        }

        int start = pos;
        int openParens = 0;
        while (pos < subject.Length)
        {
            char c = subject[pos];
            if (c == '\\' && pos + 1 < subject.Length && IsAsciiPunctuation(subject[pos + 1]))
            {
                pos += 2;
            }
            else if (c == '(')
            {
                pos++;
                openParens++;
            }
            else if (c == ')')
            {
                if (openParens < 1)
                {
                    break;
                }

                pos++;
                openParens--;
            }
            else if (c <= ' ' || c == '\u007F')
            {
                break;
            }
            else
            {
                pos++;
            }
        }

        if ((pos == start && Peek() != ')') || openParens != 0)
        {
            pos = start;
            return null;
        }

        return UnescapeString(subject[start..pos]);
    }

    private string? ParseLinkTitle()
    {
        Match m = LinkTitleRegex().Match(subject, pos);
        if (!m.Success || m.Index != pos)
        {
            return null;
        }

        pos += m.Length;
        return UnescapeString(m.Value[1..^1]);
    }

    private int ParseLinkLabel()
    {
        Match m = LinkLabelRegex().Match(subject, pos);
        if (!m.Success || m.Index != pos || m.Length > 1001)
        {
            return 0;
        }

        pos += m.Length;
        return m.Length;
    }

    private int ParseReferenceAt(Dictionary<string, (string, string?)> map)
    {
        int start = pos;
        int labelLength = ParseLinkLabel();
        if (labelLength == 0)
        {
            return 0;
        }

        string rawLabel = subject.Substring(start, labelLength);
        if (Peek() != ':')
        {
            pos = start;
            return 0;
        }

        pos++;
        SkipSpaceAndNewline();
        string? destination = ParseLinkDestination();
        if (destination is null)
        {
            pos = start;
            return 0;
        }

        int beforeTitle = pos;
        SkipSpaceAndNewline();
        string? title = null;
        if (pos != beforeTitle)
        {
            title = ParseLinkTitle();
        }

        if (title is null)
        {
            pos = beforeTitle;
        }

        bool atLineEnd = MatchSpaceToLineEnd();
        if (!atLineEnd)
        {
            if (title is null)
            {
                pos = start;
                return 0;
            }

            title = null;
            pos = beforeTitle;
            atLineEnd = MatchSpaceToLineEnd();
            if (!atLineEnd)
            {
                pos = start;
                return 0;
            }
        }

        string normalized = NormalizeLabel(rawLabel);
        if (normalized.Length == 0)
        {
            pos = start;
            return 0;
        }

        map.TryAdd(normalized, (destination, title));
        return pos - start;
    }

    private bool MatchSpaceToLineEnd()
    {
        int p = pos;
        while (p < subject.Length && subject[p] is ' ' or '\t')
        {
            p++;
        }

        if (p == subject.Length)
        {
            pos = p;
            return true;
        }

        if (subject[p] == '\n')
        {
            pos = p + 1;
            return true;
        }

        return false;
    }

    // GitHub extended autolinks: www., http(s)://, ftp:// and e-mail addresses in text outside links.
    private static void ExtendedAutolinks(MdNode node)
    {
        for (int i = 0; i < node.Children.Count; i++)
        {
            MdNode child = node.Children[i];
            if (child is MdLink or MdImage or MdCode or MdHtmlInline)
            {
                continue;
            }

            if (child is not MdText text)
            {
                ExtendedAutolinks(child);
                continue;
            }

            Match m = ExtendedAutolinkRegex().Match(text.Literal);
            while (m.Success)
            {
                int start = m.Index;
                if (start > 0 && !(char.IsWhiteSpace(text.Literal[start - 1]) || text.Literal[start - 1] is '*' or '_' or '~' or '('))
                {
                    m = m.NextMatch();
                    continue;
                }

                string candidate = TrimAutolink(m.Value);
                bool email = candidate.Contains('@') && !candidate.Contains("://", StringComparison.Ordinal) && !candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
                if (candidate.Length == 0 || !ValidAutolink(candidate, email) || (email && m.Value[^1] is '-' or '_'))
                {
                    m = m.NextMatch();
                    continue;
                }

                string destination = email ? "mailto:" + candidate : candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + candidate : candidate;
                MdLink link = new(destination, null) { Auto = true };
                link.Append(new MdText(candidate));
                string before = text.Literal[..start];
                string after = text.Literal[(start + candidate.Length)..];
                text.Literal = before;
                link.Parent = node;
                node.Children.Insert(i + 1, link);
                MdText rest = new(after) { Parent = node };
                node.Children.Insert(i + 2, rest);
                i += 1;
                break;
            }
        }
    }

    private static string TrimAutolink(string link)
    {
        while (link.Length > 0)
        {
            char last = link[^1];
            if (last is '?' or '!' or '.' or ',' or ':' or '*' or '_' or '~' or '\'' or '"')
            {
                link = link[..^1];
            }
            else if (last == ')' && link.Count(c => c == ')') > link.Count(c => c == '('))
            {
                link = link[..^1];
            }
            else if (last == ';' && TrailingEntityRegex().Match(link) is { Success: true } entity)
            {
                link = link[..entity.Index];
            }
            else
            {
                break;
            }
        }

        return link;
    }

    private static bool ValidAutolink(string link, bool email)
    {
        if (email)
        {
            int at = link.IndexOf('@');
            string domain = link[(at + 1)..];
            return at > 0 && domain.Contains('.') && !domain.EndsWith('-') && !domain.EndsWith('_') && !domain.EndsWith('.');
        }

        string host = link.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? link : link[(link.IndexOf("://", StringComparison.Ordinal) + 3)..];
        int end = host.IndexOfAny(['/', '?', '#']);
        string domainPart = end < 0 ? host : host[..end];
        string[] labels = domainPart.Split('.');
        if (domainPart.Length == 0 || (link.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && labels.Length < 2))
        {
            return false;
        }

        return labels.Length < 2 || (!labels[^1].Contains('_') && !labels[^2].Contains('_'));
    }

    /// <summary>
    /// Inline nesting deeper than <see cref="MaxDepth"/> (emphasis inside emphasis, thousands deep in hostile input) is
    /// flattened, keeping the text, so the walks that follow cannot overflow the stack.
    /// </summary>
    private static void LimitDepth(MdNode root)
    {
        Stack<(MdNode Node, int Depth)> pending = new();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            (MdNode node, int depth) = pending.Pop();
            for (int i = 0; i < node.Children.Count; i++)
            {
                MdNode child = node.Children[i];
                if (child.Children.Count == 0)
                {
                    continue;
                }

                if (depth >= MaxDepth)
                {
                    // Lift the child's children into its place, then look at the same index again.
                    List<MdNode> lifted = [.. child.Children];
                    node.Children.RemoveAt(i);
                    foreach (MdNode grandchild in lifted)
                    {
                        grandchild.Parent = node;
                    }

                    node.Children.InsertRange(i, lifted);
                    i--;
                }
                else
                {
                    pending.Push((child, depth + 1));
                }
            }
        }
    }

    /// <summary>Joins neighbouring text nodes and drops empty ones, in one pass per node list, without recursion.</summary>
    private static void MergeText(MdNode root)
    {
        Stack<MdNode> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            MdNode node = pending.Pop();
            List<MdNode> merged = new(node.Children.Count);
            StringBuilder joined = new();
            MdText? run = null;
            foreach (MdNode child in node.Children)
            {
                if (child is MdText text)
                {
                    if (run is null)
                    {
                        run = text;
                        joined.Clear().Append(text.Literal);
                        merged.Add(text);
                    }
                    else
                    {
                        joined.Append(text.Literal);
                    }

                    continue;
                }

                if (run is not null)
                {
                    run.Literal = joined.ToString();
                    run = null;
                }

                merged.Add(child);
                if (child is not MdBlock)
                {
                    pending.Push(child);
                }
            }

            if (run is not null)
            {
                run.Literal = joined.ToString();
            }

            node.Children.Clear();
            node.Children.AddRange(merged.Where(c => c is not MdText { Literal.Length: 0 }));
        }
    }

    /// <summary>The code point at <paramref name="index"/> (ending there when <paramref name="back"/>), or the lone UTF-16 unit.</summary>
    private static int CodePointAt(string text, int index, bool back)
    {
        if (back && index > 0 && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1]))
        {
            return char.ConvertToUtf32(text[index - 1], text[index]);
        }

        if (!back && index + 1 < text.Length && char.IsHighSurrogate(text[index]) && char.IsLowSurrogate(text[index + 1]))
        {
            return char.ConvertToUtf32(text[index], text[index + 1]);
        }

        return text[index];
    }

    internal static bool IsAsciiPunctuation(char c)
    {
        return c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';
    }

    private static bool IsUnicodeWhitespace(int c)
    {
        return c is ' ' or '\t' or '\n' or '\r' or '\f' || (c <= 0xFFFF && char.GetUnicodeCategory((char)c) == UnicodeCategory.SpaceSeparator)
            || (c > 0xFFFF && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator);
    }

    private static bool IsUnicodePunctuation(int c)
    {
        if (c < 128)
        {
            return IsAsciiPunctuation((char)c);
        }

        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
        return category is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol or UnicodeCategory.CurrencySymbol
            or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol;
    }

    private sealed class Delimiter(char c, int count, MdText node, bool canOpen, bool canClose)
    {
        public char Char { get; } = c;

        public int Count { get; set; } = count;

        public int OriginalCount { get; } = count;

        public MdText Node { get; } = node;

        public bool CanOpen { get; } = canOpen;

        public bool CanClose { get; } = canClose;

        public Delimiter? Previous { get; set; }

        public Delimiter? Next { get; set; }
    }

    private sealed class Bracket(MdText node, Bracket? previous, Delimiter? previousDelimiter, int index, bool image)
    {
        public MdText Node { get; } = node;

        public Bracket? Previous { get; } = previous;

        public Delimiter? PreviousDelimiter { get; } = previousDelimiter;

        public int Index { get; } = index;

        public bool Image { get; } = image;

        public bool Active { get; set; } = true;

        public bool BracketAfter { get; set; }
    }

    [GeneratedRegex(@"[ \t\r\n]+")]
    private static partial Regex WhitespaceRunRegex();

    // Patterns used with a start position are anchored there with \G, so a failed match does not scan the rest of the text.
    [GeneratedRegex(@"\G&(?:#[xX][0-9a-fA-F]{1,6};|#[0-9]{1,7};|[A-Za-z][A-Za-z0-9]{1,31};)")]
    private static partial Regex EntityRegex();

    [GeneratedRegex(@"\G<[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*>")]
    private static partial Regex EmailAutolinkRegex();

    [GeneratedRegex(@"\G<[A-Za-z][A-Za-z0-9.+-]{1,31}:[^<>\x00-\x20]*>")]
    private static partial Regex UriAutolinkRegex();

    [GeneratedRegex(@"\G(?:<[A-Za-z][A-Za-z0-9-]*(?:\s+[a-zA-Z_:][a-zA-Z0-9_.:-]*(?:\s*=\s*(?:[^""'=<>`\x00-\x20]+|'[^']*'|""[^""]*""))?)*\s*/?>|</[A-Za-z][A-Za-z0-9-]*\s*>|<!---?>|<!--[\s\S]*?-->|<\?[\s\S]*?\?>|<![A-Za-z][^>]*>|<!\[CDATA\[[\s\S]*?\]\]>)")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\G<(?:[^<>\n\\\x00]|\\.)*>")]
    private static partial Regex PointyDestinationRegex();

    [GeneratedRegex(@"\G(?:""(?:\\[\s\S]|[^""\\\x00])*""|'(?:\\[\s\S]|[^'\\\x00])*'|\((?:\\[\s\S]|[^()\\\x00])*\))")]
    private static partial Regex LinkTitleRegex();

    [GeneratedRegex(@"\G\[(?:[^\\\[\]]|\\[\s\S]){0,1000}\]")]
    private static partial Regex LinkLabelRegex();

    [GeneratedRegex(@"(?:(?:https?|ftp)://|www\.)[^\s<]*|[A-Za-z0-9.+_-]+@[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)+", RegexOptions.IgnoreCase)]
    private static partial Regex ExtendedAutolinkRegex();

    [GeneratedRegex(@"&[A-Za-z0-9]+;$")]
    private static partial Regex TrailingEntityRegex();
}
