using System.Text;

namespace Egap.Documents;

/// <summary>How <see cref="TextFormat"/> writes plain text.</summary>
public sealed class TextSaveOptions
{
    /// <summary>Writes each section's headers before its body and its footers after it, each distinct text once.</summary>
    public bool IncludeHeadersAndFooters { get; init; }

    /// <summary>Writes footnotes and endnotes at the end, each after its mark in brackets, with the mark in the text where the note is.</summary>
    public bool IncludeNotes { get; init; } = true;

    /// <summary>The line end written between paragraphs and table rows.</summary>
    public string NewLine { get; init; } = "\n";
}

/// <summary>
/// Plain text read line by line into paragraphs, and the document written as plain text in reading order, for search and indexing (Phase 6 Build Plan task 8c): one line per
/// paragraph, list labels before their text with a tab, table rows as tab-separated cells, text boxes after the paragraph
/// they are anchored in, equations as their text. Field codes, deleted and hidden text, comments and pictures are left out.
/// </summary>
public static partial class TextFormat
{
    /// <summary>
    /// Reads plain text: each line becomes a paragraph, a tab a tab and a form feed a page break. Line ends may be CR LF, LF
    /// or CR.
    /// </summary>
    public static Document Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Document document = new();
        Body body = document.AddSection().Body;
        ReadOnlySpan<char> remaining = text;
        while (true)
        {
            // Only CR and LF end a line: a form feed is a page break inside one.
            int end = remaining.IndexOfAny('\r', '\n');
            ReadOnlySpan<char> rest = end < 0 ? remaining : remaining[..end];
            Paragraph paragraph = body.AddParagraph();
            AddLine(paragraph, rest);
            if (end < 0)
            {
                break;
            }

            remaining = remaining[(remaining[end] == '\r' && end + 1 < remaining.Length && remaining[end + 1] == '\n' ? end + 2 : end + 1)..];
        }

        // A file's last line end closes its last line; it does not start an empty paragraph.
        if (text.EndsWith('\n') || text.EndsWith('\r'))
        {
            body.Blocks.RemoveAt(body.Blocks.Count - 1);
        }

        return document;
    }

    private static void AddLine(Paragraph paragraph, ReadOnlySpan<char> rest)
    {
        while (!rest.IsEmpty)
        {
            int stop = rest.IndexOfAny('\t', '\f');
            if (stop < 0)
            {
                paragraph.AddRun(rest.ToString());
                return;
            }

            if (stop > 0)
            {
                paragraph.AddRun(rest[..stop].ToString());
            }

            if (rest[stop] == '\t')
            {
                paragraph.AddTab();
            }
            else
            {
                paragraph.AddBreak(BreakKind.Page);
            }

            rest = rest[(stop + 1)..];
        }
    }

    /// <summary>Reads a plain-text file, as UTF-8 unless a byte order mark names UTF-16 or UTF-32. See <see cref="Parse(string)"/>.</summary>
    public static Document Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Document document = Parse(File.ReadAllText(path, Encoding.UTF8));
        document.FileName = path;
        return document;
    }

    /// <summary>
    /// Reads plain text from a stream, which is left open, as UTF-8 unless a byte order mark names UTF-16 or UTF-32. See
    /// <see cref="Parse(string)"/>.
    /// </summary>
    public static Document Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Writes the document as a plain-text file (UTF-8 without a byte order mark).</summary>
    /// <returns>What plain text could not hold: nothing, since everything it leaves out is left out by design.</returns>
    public static IReadOnlyList<ConversionWarning> Save(Document document, string path, TextSaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.WriteAllText(path, ToText(document, options), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return [];
    }

    /// <summary>Writes the document as plain text (UTF-8 without a byte order mark) to a stream, which is left open.</summary>
    /// <returns>What plain text could not hold: nothing, since everything it leaves out is left out by design.</returns>
    public static IReadOnlyList<ConversionWarning> Save(Document document, Stream stream, TextSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(ToText(document, options)));
        return [];
    }
}

/// <summary>Walks the model in reading order, writing each paragraph as a line.</summary>
internal sealed class PlainTextWriter(Document document, TextSaveOptions options)
{
    private readonly StringBuilder output = new();
    private readonly List<Footnote> notes = [];

    public string Write()
    {
        foreach (Section section in document.Sections)
        {
            HashSet<string> written = [];
            if (options.IncludeHeadersAndFooters)
            {
                HeadersOrFooters(section, header: true, written);
            }

            Blocks(section.Body.Blocks);
            if (options.IncludeHeadersAndFooters)
            {
                HeadersOrFooters(section, header: false, written);
            }
        }

        // A note inside a note is written with the rest, so this loop ends.
        for (int i = 0; i < notes.Count; i++)
        {
            int start = output.Length;
            Blocks(notes[i].Story.Blocks);
            output.Insert(start, $"[{notes[i].Mark}] ");
        }

        return output.ToString();
    }

    private void HeadersOrFooters(Section section, bool header, HashSet<string> written)
    {
        foreach (HeaderFooter part in section.HeadersFooters)
        {
            if (part.Kind.ToString().StartsWith("Header", StringComparison.Ordinal) != header)
            {
                continue;
            }

            int start = output.Length;
            Blocks(part.Blocks);
            string text = output.ToString(start, output.Length - start);
            if (text.Trim().Length == 0 || !written.Add(text))
            {
                output.Length = start;
            }
        }
    }

    private void Blocks(IEnumerable<Block> blocks)
    {
        foreach (Block block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    Paragraph(paragraph);
                    break;
                case Table table:
                    foreach (Row row in table.Rows)
                    {
                        output.AppendJoin('\t', row.Cells.Select(CellText)).Append(options.NewLine);
                    }

                    break;
                case ContentControl control:
                    Blocks(control.Blocks);
                    break;
                case FlexContainer flex:
                    foreach (FlexItem item in flex.Items.OrderBy(i => i.Order))
                    {
                        Blocks(item.Blocks);
                    }

                    break;
                case GridContainer grid:
                    foreach (GridItem item in grid.Items)
                    {
                        Blocks(item.Blocks);
                    }

                    break;
            }
        }
    }

    private void Paragraph(Paragraph paragraph)
    {
        List<TextBoxStory> boxes = [];
        if (paragraph.ListLabel is { Text.Length: > 0 } label)
        {
            output.Append(label.Text).Append('\t');
        }

        Inlines(paragraph.Inlines, output, new Stack<bool>(), boxes);
        output.Append(options.NewLine);
        foreach (TextBoxStory box in boxes)
        {
            Blocks(box.Blocks);
        }
    }

    /// <summary>A table cell's text on one line: its paragraphs joined by spaces, tabs and line ends made spaces.</summary>
    private string CellText(Cell cell)
    {
        StringBuilder text = new();
        foreach (Block block in cell.Blocks)
        {
            StringBuilder part = new();
            switch (block)
            {
                case Paragraph paragraph:
                    if (paragraph.ListLabel is { Text.Length: > 0 } label)
                    {
                        part.Append(label.Text).Append(' ');
                    }

                    Inlines(paragraph.Inlines, part, new Stack<bool>(), []);
                    break;
                case Table table:
                    part.AppendJoin(' ', table.Rows.SelectMany(r => r.Cells).Select(CellText));
                    break;
                case FlexContainer or GridContainer or ContentControl:
                    part.AppendJoin(' ', block.Descendants<Paragraph>().Select(p => p.GetText()));
                    break;
            }

            string line = part.ToString().Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (line.Length > 0)
            {
                text.Append(text.Length > 0 ? " " : "").Append(line);
            }
        }

        return text.ToString();
    }

    /// <summary>Appends inlines' visible text; <paramref name="fields"/> holds, per open field, whether its code is being read.</summary>
    private void Inlines(IEnumerable<Inline> inlines, StringBuilder text, Stack<bool> fields, List<TextBoxStory> boxes)
    {
        foreach (Inline inline in inlines)
        {
            if (inline.Revision is { Removes: true })
            {
                continue;
            }

            switch (inline)
            {
                case FieldStart:
                    fields.Push(true);
                    break;
                case FieldSeparator when fields.Count > 0:
                    fields.Pop();
                    fields.Push(false);
                    break;
                case FieldEnd when fields.Count > 0:
                    fields.Pop();
                    break;
                case Run run when !fields.Contains(true) && !run.GetEffectiveFormat().Hidden:
                    text.Append(run.Text);
                    break;
                case Tab when !fields.Contains(true):
                    text.Append('\t');
                    break;
                case Break when !fields.Contains(true):
                    text.Append(options.NewLine);
                    break;
                case Footnote note when options.IncludeNotes && !fields.Contains(true):
                    text.Append('[').Append(note.Mark).Append(']');
                    notes.Add(note);
                    break;
                case Island island when !fields.Contains(true):
                    // Math and other kept islands give their text, so equations can be found.
                    text.Append(island.Text);
                    break;
                case Shape { TextBox: { } box }:
                    boxes.Add(box);
                    break;
                case CompositeInline composite:
                    Inlines(composite.Inlines, text, fields, boxes);
                    break;
            }
        }
    }
}
