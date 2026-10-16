using System.Globalization;
using System.Text;

namespace Egap.Documents.Markdown;

/// <summary>
/// Writes the model as GitHub Flavored Markdown (Phase 6 design Q17) and says in warnings what Markdown cannot hold: colours,
/// fonts and sizes, merged cells, page layout, comments, drawings. Headings come from outline levels 1 to 6, code from the
/// HTML Preformatted and HTML Code styles or monospaced fonts, lists from their labels and levels.
/// </summary>
internal sealed class MarkdownWriter
{
    private static readonly HashSet<string> MonospaceFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Consolas", "Courier New", "Courier", "Cascadia Code", "Cascadia Mono", "Lucida Console", "Menlo", "Monaco", "Liberation Mono", "DejaVu Sans Mono",
    };

    private readonly Document document;
    private readonly MarkdownSaveOptions options;
    private readonly WarningCollector warnings = new();
    private readonly List<Footnote> notes = [];
    private readonly StringBuilder output = new();

    private MarkdownWriter(Document document, MarkdownSaveOptions options)
    {
        this.document = document;
        this.options = options;
    }

    public static string Write(Document document, MarkdownSaveOptions options, out IReadOnlyList<ConversionWarning> warnings)
    {
        MarkdownWriter writer = new(document, options);
        writer.WriteDocument();
        warnings = writer.warnings.ToList();
        return writer.output.ToString();
    }

    private void WriteDocument()
    {
        if (document.Extensions.Get<MarkdownExtension>(MarkdownExtension.Format) is { Kind: "front-matter", Info: { } front })
        {
            output.Append("---\n").Append(front.TrimEnd('\n')).Append("\n---\n\n");
        }
        else if (options.WriteFrontMatter && (document.Properties.Title ?? document.Properties.Author) is not null)
        {
            output.Append("---\n");
            if (document.Properties.Title is { } title)
            {
                output.Append("title: ").Append(Yaml(title)).Append('\n');
            }

            if (document.Properties.Author is { } author)
            {
                output.Append("author: ").Append(Yaml(author)).Append('\n');
            }

            output.Append("---\n\n");
        }

        foreach (Section section in document.Sections)
        {
            if (section.HeadersFooters.Any(h => h.GetText().Trim('\r', ' ').Length > 0) || !section.PageSetup.IsEmpty || section.IndexInParent > 0)
            {
                warnings.Add(ConversionWarningKind.PageLayoutDropped, "headers, footers, sections and page setup");
            }

            List<string> lines = [];
            WriteBlocks(section.Body.Blocks, lines);
            AppendChunk(lines);
        }

        // Notes that hold notes are written as their numbers, so this loop ends.
        for (int i = 0; i < notes.Count; i++)
        {
            List<string> lines = [];
            WriteBlocks(notes[i].Story.Blocks, lines);
            List<string> note = [];
            for (int j = 0; j < lines.Count; j++)
            {
                note.Add(j == 0 ? $"[^{i + 1}]: {lines[j]}" : lines[j].Length == 0 ? "" : "    " + lines[j]);
            }

            AppendChunk(note.Count == 0 ? [$"[^{i + 1}]:"] : note);
        }

        while (output.Length > 1 && output[^1] == '\n' && output[^2] == '\n')
        {
            output.Length--;
        }
    }

    private void AppendChunk(List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        if (output.Length > 0 && !(output.Length >= 2 && output[^1] == '\n' && output[^2] == '\n'))
        {
            output.Append('\n');
        }

        foreach (string line in lines)
        {
            output.Append(line).Append('\n');
        }

        output.Append('\n');
    }

    /// <summary>One block ready to write: its containers (quotes and list items), its kind and its lines without prefixes.</summary>
    private sealed record OutBlock(IReadOnlyList<MarkdownFrame> Frames, string Kind, List<string> Lines);

    /// <summary>The blocks of one container as Markdown lines: each block is prefixed by its quotes and list items.</summary>
    private void WriteBlocks(IEnumerable<Block> blocks, List<string> lines)
    {
        List<OutBlock> collected = [];
        Collect([.. blocks], collected, new FrameState());
        Emit(collected, lines);
    }

    /// <summary>Works out containers for blocks that did not come from Markdown (a DOCX, say): lists from labels, quotes from styles.</summary>
    private sealed class FrameState
    {
        public MarkdownFrame?[] Items { get; } = new MarkdownFrame?[9];

        public List<MarkdownFrame> Quotes { get; } = [];

        public int NextId { get; set; } = 1_000_000;
    }

    private void Collect(List<Block> list, List<OutBlock> collected, FrameState state)
    {
        for (int i = 0; i < list.Count; i++)
        {
            Block block = list[i];
            switch (block)
            {
                case Paragraph paragraph when IsCode(paragraph):
                    {
                        List<Paragraph> run = [paragraph];
                        while (i + 1 < list.Count && list[i + 1] is Paragraph next && IsCode(next) && next.Extensions.Get<MarkdownExtension>(MarkdownExtension.Format) is null)
                        {
                            run.Add(next);
                            i++;
                        }

                        List<string> code = [];
                        WriteCode(run, code, 0);
                        collected.Add(new OutBlock(FramesOf(paragraph, state), "code", code));
                        break;
                    }

                case Paragraph paragraph when paragraph.ListLabel is not null:
                    collected.Add(new OutBlock(FramesOf(paragraph, state), "item", InlineLines(paragraph, task: true)));
                    break;
                case Paragraph paragraph:
                    {
                        List<string> text = ParagraphLines(paragraph);
                        string kind = text is ["---"] ? "hr" : text is [{ } first] && first.StartsWith('#') && paragraph.GetEffectiveFormat().OutlineLevel <= 5 ? "h" : "p";
                        collected.Add(new OutBlock(FramesOf(paragraph, state), kind, text));
                        break;
                    }

                case Table table:
                    {
                        List<string> rows = [];
                        WriteTable(table, rows);
                        collected.Add(new OutBlock(FramesOf(table, state), "table", rows));
                        break;
                    }

                case BlockIsland island:
                    if (island.Format == "html")
                    {
                        collected.Add(new OutBlock(FramesOf(island, state), "html", [.. island.Markup.Split('\n')]));
                    }
                    else
                    {
                        warnings.Add(ConversionWarningKind.DrawingDropped, "kept blocks of other formats");
                    }

                    break;
                case ContentControl control:
                    warnings.Add(ConversionWarningKind.ContentControlsDropped, "content controls, written as their content");
                    Collect([.. control.Blocks], collected, state);
                    break;
            }
        }
    }

    private static IReadOnlyList<MarkdownFrame> FramesOf(Block block, FrameState state)
    {
        if (block.Extensions.Get<MarkdownExtension>(MarkdownExtension.Format) is { } extension)
        {
            return extension.Frames;
        }

        Paragraph? paragraph = block as Paragraph;
        int depth = paragraph is null ? 0 : QuoteDepth(paragraph);
        while (state.Quotes.Count > depth)
        {
            state.Quotes.RemoveAt(state.Quotes.Count - 1);
        }

        while (state.Quotes.Count < depth)
        {
            state.Quotes.Add(new MarkdownFrame('Q', state.NextId++));
        }

        List<MarkdownFrame> frames = [.. state.Quotes];
        if (paragraph?.ListLabel is { } label)
        {
            int level = label.Level;
            for (int i = 0; i < level; i++)
            {
                state.Items[i] ??= new MarkdownFrame('I', state.NextId++, "-", true, -1);
                frames.Add(state.Items[i]!);
            }

            // Lists from other formats are written tight: one paragraph per item is what they almost always hold.
            MarkdownFrame item = new('I', state.NextId++, Marker(paragraph, label), true, label.List.Id * 16 + level);
            state.Items[level] = item;
            Array.Clear(state.Items, level + 1, state.Items.Length - level - 1);
            frames.Add(item);
            return frames;
        }

        // A paragraph indented to a list level continues the item open at that level.
        if (paragraph?.Format.LeftIndent is double indent && indent > 0)
        {
            int level = Math.Clamp((int)Math.Round(indent / 36) - 1, 0, 8);
            if (state.Items[level] is not null)
            {
                for (int i = 0; i <= level; i++)
                {
                    frames.Add(state.Items[i] ?? new MarkdownFrame('I', state.NextId++, "-", true, -1));
                }

                return frames;
            }
        }

        Array.Clear(state.Items);
        return frames;
    }

    private static void Emit(List<OutBlock> blocks, List<string> lines)
    {
        IReadOnlyList<MarkdownFrame> open = [];
        OutBlock? previous = null;
        Dictionary<int, string> markers = [];
        Dictionary<int, (int List, char Char)> lastAtDepth = [];
        foreach (OutBlock block in blocks)
        {
            IReadOnlyList<MarkdownFrame> frames = block.Frames;
            int common = 0;
            while (common < open.Count && common < frames.Count && open[common].Id == frames[common].Id)
            {
                common++;
            }

            if (previous is not null)
            {
                bool blank = true;
                int k = -1;
                for (int i = frames.Count - 1; i >= 0; i--)
                {
                    if (frames[i].Kind == 'I')
                    {
                        k = i;
                        break;
                    }
                }

                if (previous.Lines.Count == 0 && common == frames.Count && open.Count == frames.Count)
                {
                    // An item opened by an empty paragraph: its first block goes right under the marker.
                    blank = false;
                }
                else if (k >= 0)
                {
                    // The item both blocks are in, if any, is the innermost item frame they share.
                    int shared = -1;
                    for (int i = Math.Min(common, frames.Count) - 1; i >= 0; i--)
                    {
                        if (frames[i].Kind == 'I')
                        {
                            shared = i;
                            break;
                        }
                    }

                    if (k >= common)
                    {
                        bool sibling = common == k && open.Count > k && open[k].Kind == 'I' && open[k].List == frames[k].List;
                        bool firstNested = common == k && open.Count == k && k > 0 && frames[k - 1].Kind == 'I';
                        blank = sibling ? !frames[k].Tight
                            : firstNested ? shared >= 0 && !frames[shared].Tight
                            : true;
                    }
                    else
                    {
                        blank = !frames[k].Tight || (previous.Kind == "p" && block.Kind is "p" or "item");
                    }
                }

                if (blank)
                {
                    lines.Add(Prefix(frames, common, isFirstLine: false, markers).TrimEnd());
                }
            }

            // A new list right after another at the same depth would join it: give it the other marker character.
            for (int i = common; i < frames.Count; i++)
            {
                MarkdownFrame frame = frames[i];
                if (frame.Kind != 'I')
                {
                    continue;
                }

                if (!markers.TryGetValue(frame.List, out string? chosen))
                {
                    string marker = frame.Marker;
                    if (lastAtDepth.TryGetValue(i, out (int List, char Char) last) && last.List != frame.List && last.Char == marker[^1])
                    {
                        marker = marker[^1] switch
                        {
                            '-' => "*",
                            '*' or '+' => "-",
                            '.' => marker[..^1] + ")",
                            _ => marker[..^1] + ".",
                        };
                    }

                    chosen = marker[^1].ToString();
                    markers[frame.List] = chosen;
                }

                lastAtDepth[i] = (frame.List, chosen[^1]);
            }

            if (block.Lines.Count == 0)
            {
                lines.Add(Prefix(frames, common, isFirstLine: true, markers).TrimEnd());
            }

            for (int j = 0; j < block.Lines.Count; j++)
            {
                string prefix = Prefix(frames, common, j == 0, markers);
                string line = block.Lines[j];
                lines.Add(line.Length == 0 ? prefix.TrimEnd() : prefix + line);
            }

            open = frames;
            previous = block;
        }
    }

    /// <summary>
    /// The prefix for a line: "> " per quote, and per list item its marker on the first line of a newly opened item, else
    /// spaces as wide as the marker.
    /// </summary>
    private static string Prefix(IReadOnlyList<MarkdownFrame> frames, int common, bool isFirstLine, Dictionary<int, string> markers)
    {
        StringBuilder prefix = new();
        for (int i = 0; i < frames.Count; i++)
        {
            MarkdownFrame frame = frames[i];
            if (frame.Kind == 'Q')
            {
                prefix.Append("> ");
                continue;
            }

            string marker = frame.Marker;
            if (markers.TryGetValue(frame.List, out string? chosen) && chosen[^1] != marker[^1])
            {
                marker = marker[..^1] + chosen[^1];
            }

            prefix.Append(i >= common && isFirstLine ? marker + " " : new string(' ', marker.Length + 1));
        }

        return prefix.ToString();
    }

    private static int QuoteDepth(Paragraph paragraph)
    {
        if (paragraph.Style?.Id != "BlockQuote")
        {
            return 0;
        }

        return Math.Max(1, (int)Math.Round(paragraph.GetEffectiveFormat().LeftIndent / 36));
    }

    private static bool IsCode(Paragraph paragraph)
    {
        if (paragraph.Extensions.Get<MarkdownExtension>(MarkdownExtension.Format) is { Kind: "code" })
        {
            return true;
        }

        return paragraph.ListLabel is null && paragraph.Style is { } style && style.BasedOnChain().Any(s => s.Id is "HTMLPreformatted" or "HTMLPreformattedChar" or "SourceCode" or "Code");
    }

    private static string Marker(Paragraph paragraph, ListLabel label)
    {
        if (label.Format.NumberStyle == NumberStyle.Bullet)
        {
            char bullet = paragraph.Extensions.Get<MarkdownExtension>(MarkdownExtension.Format)?.Marker is char c and ('-' or '*' or '+') ? c : '-';
            return bullet.ToString();
        }

        string text = label.Text.TrimEnd('.', ')');
        char delimiter = paragraph.Extensions.Get<MarkdownExtension>(MarkdownExtension.Format)?.Marker is ')' ? ')' : '.';
        return (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? Math.Min(number, 999_999_999) : 1).ToString(CultureInfo.InvariantCulture) + delimiter;
    }

    private List<string> ParagraphLines(Paragraph paragraph)
    {
        if (paragraph.Extensions.Get<MarkdownExtension>(MarkdownExtension.Format) is { Kind: "hr" } || IsRule(paragraph))
        {
            return ["---"];
        }

        int outline = paragraph.GetEffectiveFormat().OutlineLevel;
        List<string> text = InlineLines(paragraph, task: false);
        if (outline is >= 0 and <= 5)
        {
            string line = string.Join(" ", text.Select(l => l.EndsWith('\\') ? l[..^1] : l)).Trim();

            // A closing run of # would be read as the heading's closing sequence.
            int hashes = 0;
            while (hashes < line.Length && line[line.Length - 1 - hashes] == '#')
            {
                hashes++;
            }

            if (hashes > 0 && (hashes == line.Length || line[line.Length - 1 - hashes] is ' ' or '\t'))
            {
                line = line[..^hashes] + "\\" + line[^hashes..];
            }

            return [new string('#', outline + 1) + (line.Length > 0 ? " " + line : "")];
        }

        if (paragraph.Format.Alignment is ParagraphAlignment.Center or ParagraphAlignment.Right)
        {
            warnings.Add(ConversionWarningKind.FormattingDropped, "paragraph alignment");
        }

        return text;
    }

    private static bool IsRule(Paragraph paragraph)
    {
        return paragraph.Inlines.Count == 0 && paragraph.Format.BorderBottom is { IsVisible: true } && paragraph.Format.BorderTop is null;
    }

    private static void WriteCode(List<Paragraph> paragraphs, List<string> lines, int depth)
    {
        List<string> code = [];
        foreach (Paragraph paragraph in paragraphs)
        {
            StringBuilder line = new();
            foreach (Node node in paragraph.Descendants())
            {
                switch (node)
                {
                    case Run run when run.Revision is not { Removes: true }:
                        line.Append(run.Text);
                        break;
                    case Tab:
                        line.Append('\t');
                        break;
                    case Break:
                        code.Add(line.ToString());
                        line.Clear();
                        break;
                }
            }

            code.Add(line.ToString());
        }

        string info = paragraphs[0].Extensions.Get<MarkdownExtension>(MarkdownExtension.Format)?.Info ?? "";
        char fenceChar = info.Contains('`') ? '~' : '`';
        int longest = code.Select(l => LongestRun(l, fenceChar)).DefaultIfEmpty(0).Max();
        string fence = new(fenceChar, Math.Max(3, longest + 1));
        string prefix = depth > 0 ? string.Concat(Enumerable.Repeat("> ", depth)) : "";
        lines.Add(prefix + fence + info);
        lines.AddRange(code.Select(l => prefix + l));
        lines.Add(prefix + fence);
    }

    private void WriteTable(Table table, List<string> lines)
    {
        List<List<string>> rows = [];
        List<ParagraphAlignment?> alignments = [];
        foreach (Row row in table.Rows)
        {
            List<string> cells = [];
            foreach (Cell cell in row.Cells)
            {
                if (cell.Format.GridSpan > 1 || cell.Format.VerticalMerge is VerticalMerge.Restart or VerticalMerge.Continue)
                {
                    warnings.Add(ConversionWarningKind.MergedCellsSplit, "merged table cells");
                }

                List<string> parts = [];
                foreach (Block block in cell.Blocks)
                {
                    if (block is Paragraph p)
                    {
                        parts.Add(string.Join(" ", InlineLines(p, task: false, header: rows.Count == 0)));
                    }
                    else
                    {
                        warnings.Add(ConversionWarningKind.TableSimplified, "nested tables and blocks in cells, written as text");
                        parts.Add(Escape(block.GetText().Replace(ControlChar.ParagraphBreak, ' ').Trim(), atLineStart: false));
                    }
                }

                if (parts.Count > 1)
                {
                    warnings.Add(ConversionWarningKind.TableSimplified, "paragraphs in table cells, joined with line breaks");
                }

                cells.Add(string.Join("<br>", parts.Where(p => p.Length > 0)).Replace("|", "\\|", StringComparison.Ordinal));
                int column = cells.Count - 1;
                while (alignments.Count <= column)
                {
                    alignments.Add(null);
                }

                if (rows.Count == 1 || (rows.Count == 0 && alignments[column] is null))
                {
                    alignments[column] = cell.Paragraphs.FirstOrDefault()?.Format.Alignment;
                }
            }

            rows.Add(cells);
        }

        if (rows.Count == 0)
        {
            return;
        }

        int columns = Math.Max(1, rows.Max(r => r.Count));
        foreach (List<string> row in rows)
        {
            while (row.Count < columns)
            {
                row.Add("");
            }
        }

        lines.Add("| " + string.Join(" | ", rows[0]) + " |");
        lines.Add("|" + string.Join("|", Enumerable.Range(0, columns).Select(c => (c < alignments.Count ? alignments[c] : null) switch
        {
            ParagraphAlignment.Center => ":-:",
            ParagraphAlignment.Right => "--:",
            ParagraphAlignment.Left => ":--",
            _ => "---",
        })) + "|");
        foreach (List<string> row in rows.Skip(1))
        {
            lines.Add("| " + string.Join(" | ", row) + " |");
        }
    }

    // Inlines.

    /// <summary>The paragraph's inlines as Markdown, split into lines at line breaks (each ending with a backslash but the last).</summary>
    private List<string> InlineLines(Paragraph paragraph, bool task, bool header = false)
    {
        StringBuilder text = new();
        InlineState state = new() { Outer = (header, false, false) };
        bool first = true;
        foreach (Inline inline in paragraph.Inlines)
        {
            if (task && first && inline is InlineContentControl { Properties.Kind: ContentControlKind.CheckBox } box)
            {
                text.Append(box.Properties.IsChecked == true ? "[x] " : "[ ] ");
                first = false;
                state.SkipLeadingSpace = true;
                continue;
            }

            first = false;
            WriteInline(inline, text, state);
        }

        state.Close(text);
        string result = text.ToString().TrimEnd(' ', '\t');
        if (result.Length == 0)
        {
            return [];
        }

        // White space at the start of the paragraph is part of its text only when written as character references.
        int leadingWhite = 0;
        while (leadingWhite < result.Length && result[leadingWhite] is ' ' or '\t')
        {
            leadingWhite++;
        }

        if (leadingWhite > 0)
        {
            result = string.Concat(result[..leadingWhite].Select(c => c == '\t' ? "&#9;" : "&#32;")) + result[leadingWhite..];
        }

        string[] lines = result.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = EscapeLineStart(lines[i].TrimStart(' ', '\t'));
        }

        return [.. lines];
    }

    private void WriteInline(Inline inline, StringBuilder text, InlineState state)
    {
        if (inline.Revision is { } revision)
        {
            warnings.Add(ConversionWarningKind.ChangesAccepted, "tracked changes, written as if accepted");
            if (revision.Removes)
            {
                return;
            }
        }

        if (state.FieldCodeDepth > 0 && inline is not (FieldStart or FieldSeparator or FieldEnd))
        {
            return;
        }

        switch (inline)
        {
            case Run run:
                WriteText(run, run.Text, text, state);
                break;
            case Tab:
                state.Close(text);
                text.Append('\t');
                break;
            case Break b:
                if (b.Kind == BreakKind.Line)
                {
                    // A hard break may sit inside emphasis; spaces before it would be lost, so they go first.
                    text.Append(state.PendingSpace).Append("\\\n");
                    state.PendingSpace = "";
                }
                else
                {
                    state.Close(text);
                    warnings.Add(ConversionWarningKind.PageLayoutDropped, "page and column breaks");
                    text.Append(' ');
                }

                break;
            case Hyperlink link:
                {
                    List<Run> linkRuns = [.. link.Descendants<Run>()];
                    (bool Bold, bool Italic, bool Strike, bool Code)[] linkFlags = [.. linkRuns.Select(Flags)];
                    bool shareBold = linkFlags.Length > 0 && linkFlags.All(x => x.Bold);
                    bool shareItalic = linkFlags.Length > 0 && linkFlags.All(x => x.Italic);
                    bool shareStrike = linkFlags.Length > 0 && linkFlags.All(x => x.Strike);
                    state.Set(text, shareBold, shareItalic, shareStrike, "");
                    StringBuilder inner = new();
                    InlineState innerState = new() { Outer = (shareBold, shareItalic, shareStrike) };
                    foreach (Inline child in link.Inlines)
                    {
                        WriteInline(child, inner, innerState);
                    }

                    innerState.Close(inner);
                    string destination = link.Address is { } address ? address + (link.Anchor is { } a ? "#" + a : "") : "#" + link.Anchor;
                    if (inner.ToString() == Escape(destination, atLineStart: false) && link.Address is not null && link.Tooltip is null && Uri.TryCreate(destination, UriKind.Absolute, out _) && !destination.Contains('>'))
                    {
                        text.Append('<').Append(destination).Append('>');
                    }
                    else
                    {
                        text.Append('[').Append(inner).Append("](").Append(Destination(destination)).Append(Title(link.Tooltip)).Append(')');
                    }

                    break;
                }

            case InlineContentControl control:
                warnings.Add(ConversionWarningKind.ContentControlsDropped, "content controls, written as their content");
                foreach (Inline child in control.Inlines)
                {
                    WriteInline(child, text, state);
                }

                break;
            case Picture picture:
                {
                    string? url = picture.SourceUrl ?? options.ImageWriter?.Invoke(picture);
                    if (url is null && options.EmbedImages && picture.Image.Bytes.Length > 0)
                    {
                        url = $"data:{picture.Image.ContentType};base64,{Convert.ToBase64String(picture.Image.Bytes.Span)}";
                    }

                    if (url is null)
                    {
                        warnings.Add(ConversionWarningKind.PictureDropped, "pictures without an address (give MarkdownSaveOptions.ImageWriter)");
                        break;
                    }

                    state.Close(text);
                    text.Append("![").Append(Escape(picture.Description ?? "", atLineStart: false)).Append("](").Append(Destination(url)).Append(Title(picture.Title)).Append(')');
                    break;
                }

            case Footnote note:
                state.Close(text);
                if (note.OwnerStory is NoteStory)
                {
                    text.Append(Escape(note.Mark, atLineStart: false));
                    break;
                }

                notes.Add(note);
                text.Append("[^").Append(notes.Count.ToString(CultureInfo.InvariantCulture)).Append(']');
                break;
            case Comment:
                warnings.Add(ConversionWarningKind.CommentsDropped, "comments");
                break;
            case Island island when island.Format == "html":
                state.Close(text);
                text.Append(island.Markup);
                break;
            case Island island:
                warnings.Add(ConversionWarningKind.DrawingDropped, "equations, charts and other kept objects" + (island.Text.Length > 0 ? ", written as their text" : ""));
                WriteText(null, island.Text, text, state);
                break;
            case Shape:
                warnings.Add(ConversionWarningKind.DrawingDropped, "shapes and text boxes");
                break;
            case FieldStart:
                state.FieldCodeDepth++;
                state.FieldCodes.Push(true);
                warnings.Add(ConversionWarningKind.FieldsFlattened, "fields, written as their results");
                break;
            case FieldSeparator when state.FieldCodes.Count > 0:
                state.FieldCodes.Pop();
                state.FieldCodes.Push(false);
                state.FieldCodeDepth--;
                break;
            case FieldEnd when state.FieldCodes.Count > 0:
                if (state.FieldCodes.Pop())
                {
                    state.FieldCodeDepth--;
                }

                break;
        }
    }

    private void WriteText(Run? run, string value, StringBuilder text, InlineState state)
    {
        if (value.Length == 0)
        {
            return;
        }

        (bool bold, bool italic, bool strike, bool code) = run is null ? (false, false, false, false) : Flags(run);
        if (code)
        {
            // A code span from Markdown keeps its spaces (CommonMark's padding rule takes one off each side when both have
            // one); monospaced text from elsewhere keeps its spaces outside the span.
            bool span = run!.Style?.BasedOnChain().Any(s => s.Id == "HTMLCode") == true;
            if (span)
            {
                state.Set(text, bold, italic, strike, "");
                string fenceText = new('`', LongestRun(value, '`') + 1);
                bool padded = value.StartsWith('`') || value.EndsWith('`') || (value.Trim(' ').Length > 0 && value.StartsWith(' ') && value.EndsWith(' '));
                text.Append(fenceText).Append(padded ? " " : "").Append(value.Replace('\n', ' ')).Append(padded ? " " : "").Append(fenceText);
                return;
            }

            string inner = value.Trim(' ');
            if (inner.Length == 0)
            {
                state.Close(text);
                text.Append(value);
                return;
            }

            string before = value[..value.IndexOf(inner[0])];
            string after = value[(before.Length + inner.Length)..];
            state.Set(text, bold, italic, strike, before);
            int longest = LongestRun(inner, '`');
            string fence = new('`', longest + 1);
            bool pad = inner.StartsWith('`') || inner.EndsWith('`');
            text.Append(fence).Append(pad ? " " : "").Append(inner.Replace('\n', ' ')).Append(pad ? " " : "").Append(fence);
            state.PendingSpace = after;
            return;
        }

        if (state.SkipLeadingSpace)
        {
            value = value.TrimStart(' ');
            state.SkipLeadingSpace = false;
            if (value.Length == 0)
            {
                return;
            }
        }

        // Spaces stay outside emphasis markers, which cannot sit next to white space.
        string trimmedStart = value.TrimStart(' ', '\t');
        string leading = value[..(value.Length - trimmedStart.Length)];
        string core = trimmedStart.TrimEnd(' ', '\t');
        string trailing = trimmedStart[core.Length..];
        if (core.Length == 0)
        {
            state.Close(text);
            text.Append(value);
            return;
        }

        state.Set(text, bold, italic, strike, leading);
        text.Append(Escape(core, atLineStart: false));
        state.PendingSpace = trailing;
    }

    private (bool Bold, bool Italic, bool Strike, bool Code) Flags(Run run)
    {
        RunFormat direct = run.Format;
        bool? bold = direct.Bold, italic = direct.Italic, strike = direct.Strikethrough;
        string? font = direct.FontAscii;
        bool codeStyle = false;
        for (Style? s = run.Style; s is not null; s = s.BasedOn)
        {
            RunFormat format = ((CharacterStyle)s).RunFormat;
            bold ??= format.Bold;
            italic ??= format.Italic;
            strike ??= format.Strikethrough;
            font ??= format.FontAscii;
            codeStyle |= s.Id is "HTMLCode" or "VerbatimChar" or "Code" or "InlineCode";
        }

        bool code = codeStyle || (font is not null && MonospaceFonts.Contains(font));
        if (direct.Color is not null || direct.Size is not null || direct.Highlight is not null || direct.Underline is not null and not UnderlineStyle.None || (direct.FontAscii is { } f && !MonospaceFonts.Contains(f)))
        {
            warnings.Add(ConversionWarningKind.FormattingDropped, "colours, fonts, sizes, underlines and highlights");
        }

        return (bold == true, italic == true, strike == true, code);
    }

    /// <summary>The markers open for the text written so far, closed and opened as formatting changes.</summary>
    private sealed class InlineState
    {
        private readonly List<string> open = [];

        public string PendingSpace { get; set; } = "";

        public bool SkipLeadingSpace { get; set; }

        public int FieldCodeDepth { get; set; }

        public Stack<bool> FieldCodes { get; } = new();

        public (bool Bold, bool Italic, bool Strike) Outer { get; init; }

        public void Set(StringBuilder text, bool bold, bool italic, bool strike, string leading)
        {
            List<string> wanted = [];
            if (strike && !Outer.Strike)
            {
                wanted.Add("~~");
            }

            if (bold && !Outer.Bold)
            {
                wanted.Add("**");
            }

            if (italic && !Outer.Italic)
            {
                wanted.Add("*");
            }

            // Keep the open markers that are still wanted (as far as they nest), close the rest, then open what is missing.
            int keep = 0;
            while (keep < open.Count && wanted.Contains(open[keep]))
            {
                keep++;
            }

            CloseTo(text, keep);
            text.Append(PendingSpace).Append(leading);
            PendingSpace = "";
            foreach (string marker in wanted)
            {
                if (!open.Contains(marker))
                {
                    text.Append(marker);
                    open.Add(marker);
                }
            }
        }

        public void Close(StringBuilder text)
        {
            CloseTo(text, 0);
            text.Append(PendingSpace);
            PendingSpace = "";
        }

        private void CloseTo(StringBuilder text, int keep)
        {
            for (int i = open.Count - 1; i >= keep; i--)
            {
                text.Append(open[i]);
                open.RemoveAt(i);
            }
        }
    }

    private static int LongestRun(string text, char c)
    {
        int longest = 0, current = 0;
        foreach (char ch in text)
        {
            current = ch == c ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    /// <summary>Backslash-escapes what Markdown would read as markup.</summary>
    internal static string Escape(string text, bool atLineStart)
    {
        StringBuilder result = new(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool entity = c == '&' && i + 2 < text.Length && (text[i + 1] == '#' || char.IsAsciiLetter(text[i + 1])) && text.IndexOf(';', i + 1) is > 0 and var semicolon && semicolon - i <= 33;
            if (c is '\n' or '\r')
            {
                result.Append(c == '\n' ? "&#10;" : "&#13;");
                continue;
            }

            if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '~' or '|' || entity)
            {
                result.Append('\\');
            }

            result.Append(c);
        }

        return atLineStart ? EscapeLineStart(result.ToString()) : result.ToString();
    }

    /// <summary>Escapes what would start a block at the start of a line: headings, quotes, list markers, rules, setext underlines.</summary>
    private static string EscapeLineStart(string line)
    {
        if (line.Length == 0)
        {
            return line;
        }

        char c = line[0];
        if (c is '#' or '+' or '-' or '=' or '>')
        {
            return "\\" + line;
        }

        int digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        return digits > 0 && digits < line.Length && line[digits] is '.' or ')' ? line[..digits] + "\\" + line[digits..] : line;
    }

    private static string Destination(string url)
    {
        bool angle = url.Any(c => c is ' ' or '(' or ')' or '<' or '>' or '\n' || char.IsControl(c));
        if (!angle)
        {
            return url.Replace("\\", "\\\\", StringComparison.Ordinal);
        }

        return "<" + url.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("<", "\\<", StringComparison.Ordinal).Replace(">", "\\>", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) + ">";
    }

    private static string Title(string? title)
    {
        return title is null ? "" : " \"" + title.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string Yaml(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
