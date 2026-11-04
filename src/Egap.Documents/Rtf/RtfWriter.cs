using System.Globalization;
using System.Text;

namespace Egap.Documents.Rtf;

/// <summary>
/// Writes the model as RTF 1.9.1, as Word 2007 and later write it and read it back: the header tables (fonts, colours, the
/// style sheet, lists and list overrides, revision authors, document information), then each section with its headers and
/// footers, its paragraphs and tables. Text is 7-bit: every other character is a <c>\u</c> escape with a one-character fallback.
/// Paragraphs and runs carry the formatting their styles give as well as their own, as RTF wants (see
/// <see cref="RtfStyleFormats"/>). What the reader kept as islands and extension data is written back as it was read.
/// </summary>
internal sealed partial class RtfWriter
{
    private readonly Document document;
    private readonly RtfSaveOptions options;
    private readonly WarningCollector warnings = new();
    private readonly RtfStyleFormats.Cache given;
    private StringBuilder output = new();

    /// <summary>A control word was written last, so text that could continue it needs the space that ends it.</summary>
    private bool pendingSpace;

    private RtfWriter(Document document, RtfSaveOptions options)
    {
        this.document = document;
        this.options = options;
        given = new RtfStyleFormats.Cache(document);
    }

    public static IReadOnlyList<ConversionWarning> Write(Document document, Stream stream, RtfSaveOptions options)
    {
        RtfWriter writer = new(document, options);
        writer.WriteDocument();

        // Every character written is below U+0100 (escapes for the rest, islands kept as their bytes), so Latin-1 is exact.
        byte[] buffer = new byte[16384];
        foreach (ReadOnlyMemory<char> chunk in writer.output.GetChunks())
        {
            ReadOnlySpan<char> span = chunk.Span;
            while (span.Length > 0)
            {
                int count = Math.Min(span.Length, buffer.Length);
                int written = Encoding.Latin1.GetBytes(span[..count], buffer);
                stream.Write(buffer, 0, written);
                span = span[count..];
            }
        }

        return writer.warnings.ToList();
    }

    private void WriteDocument()
    {
        // The body first: writing it finds the fonts, colours and authors the header tables list.
        StringBuilder body = output = new StringBuilder(4096);
        WriteSections();
        StringBuilder tables = output = new StringBuilder(4096);
        WriteDefaults();
        WriteStyleSheet();
        WriteLists();
        WriteRevisionTable();
        WriteProtectionUsers();
        WriteInfo();
        WriteDocumentSettings();
        WriteKeptGroups();

        output = new StringBuilder(body.Length + tables.Length + 4096);
        Open();
        Word("rtf", 1);
        Word("ansi");
        Word("ansicpg", 1252);
        Word("uc", 1);
        WriteDefaultFont();
        WriteFontTable();
        WriteColorTable();
        output.Append(tables);
        output.Append(body);
        Close();
    }

    // ---- Sections and stories ----

    private void WriteSections()
    {
        int count = document.Sections.Count;
        if (document.Sections.Any(s => s.MarginBoxes.Count > 0))
        {
            warnings.Add(ConversionWarningKind.PageLayoutDropped, "RTF has no margin boxes; they were left out.");
        }

        for (int i = 0; i < count; i++)
        {
            Section section = document.Sections[i];
            Word("sectd");
            SectionWords(section.PageSetup);
            WriteHeadersFooters(section);
            // An empty last section needs a paragraph, or it is no section at all.
            WriteStory(section.Body.Blocks, i < count - 1 ? "sect" : i > 0 && section.Body.Blocks.Count == 0 ? "par" : null);
            output.AppendLine();
        }
    }

    private void WriteHeadersFooters(Section section)
    {
        foreach (HeaderFooter headerFooter in section.HeadersFooters)
        {
            string word = headerFooter.Kind switch
            {
                HeaderFooterKind.HeaderEven => "headerl",
                HeaderFooterKind.HeaderFirst => "headerf",
                HeaderFooterKind.FooterPrimary => "footerr",
                HeaderFooterKind.FooterEven => "footerl",
                HeaderFooterKind.FooterFirst => "footerf",
                _ => "headerr",
            };
            Open();
            Word(word);
            WriteStory(headerFooter.Blocks, null);
            Close();
        }
    }

    /// <summary>
    /// Writes a story's blocks. Its last paragraph ends with <paramref name="last"/> (<c>\sect</c> for a section that is not
    /// the document's last); with null, a last paragraph that has content needs no paragraph mark, as Word writes it.
    /// </summary>
    private void WriteStory(NodeCollection<Block> blocks, string? last)
    {
        List<Block> flat = Flatten(blocks);
        for (int i = 0; i < flat.Count; i++)
        {
            bool isLast = i == flat.Count - 1;
            switch (flat[i])
            {
                case Paragraph paragraph:
                    WriteParagraph(paragraph, 0, isLast ? last ?? (HasContent(paragraph) ? null : "par") : "par");
                    break;
                case Table table:
                    WriteTable(table, 1);
                    if (isLast && last is not null)
                    {
                        // A section ends at a paragraph mark: an empty one after the table.
                        Word("pard");
                        Word("plain");
                        Word(last);
                    }

                    break;
            }
        }

        if (flat.Count == 0 && last is not null)
        {
            Word("pard");
            Word("plain");
            Word(last);
        }

        CloseFields();
    }

    /// <summary>
    /// The blocks as RTF can hold them: paragraphs and tables. Content controls give their blocks (or the table a boxed one
    /// draws), flex and grid containers the borderless table DOCX writes for them; other islands give nothing.
    /// </summary>
    private List<Block> Flatten(NodeCollection<Block> blocks)
    {
        List<Block> result = new(blocks.Count);
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
                    warnings.Add(ConversionWarningKind.TableSimplified, "RTF has no flex or grid containers; each became a borderless table.");
                    if (Docx.ContainerTables.For(block, Docx.ContainerTables.TextWidth(block)) is { } table)
                    {
                        result.Add(table);
                    }

                    break;
                case ContentControl control:
                    warnings.Add(ConversionWarningKind.ContentControlsDropped, "RTF has no content controls; their content was kept.");
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
                case BlockIsland island when island.Format == RtfExtension.Format:
                    // Read from RTF and kept as it was: written back as a paragraph that holds it.
                    result.Add(IslandParagraph(island));
                    break;
                default:
                    warnings.Add(ConversionWarningKind.DrawingDropped, "A block kept from another format has no place in RTF and was left out.");
                    break;
            }
        }
    }

    private Paragraph IslandParagraph(BlockIsland island)
    {
        Paragraph paragraph = new(document);
        RtfExtension.Of(paragraph).Inline = [(0, island.Markup)];
        return paragraph;
    }

    private static bool HasContent(Paragraph paragraph)
    {
        // Field characters alone may write nothing (an end whose field a section break closed), so they do not count.
        foreach (Inline inline in paragraph.Inlines)
        {
            if (inline is not (FieldStart or FieldSeparator or FieldEnd))
            {
                return true;
            }
        }

        return paragraph.Extensions.Get<RtfExtension>(RtfExtension.Format)?.Inline is { Count: > 0 };
    }

    // ---- Paragraphs ----

    /// <summary>
    /// Writes a paragraph at table level <paramref name="level"/>, ended by <paramref name="end"/> (<c>par</c>, <c>cell</c>,
    /// <c>nestcell</c> or <c>sect</c>), or by nothing when null: the story's last paragraph.
    /// </summary>
    private void WriteParagraph(Paragraph paragraph, int level, string? end)
    {
        current = paragraph;
        currentLevel = level;
        paragraphSerial++;
        ParagraphProperties(paragraph, level);
        if (noteMarkPending)
        {
            // A note's text starts with its number, which the note's reference gives it.
            noteMarkPending = false;
            Open();
            Word("super");
            Word("chftn");
            Close();
        }

        WriteInlines(paragraph);
        if (end is "cell" or "nestcell" or "sect")
        {
            // A field can't go on past the end of its cell or section.
            CloseFields();
        }

        // The paragraph mark's formatting and revision, which the paragraph mark takes from the state at its end.
        ParagraphStyle? style = paragraph.Style;
        RunWords(Effective(given.Run(style, null, mark: true), paragraph.MarkFormat), null);
        bool marked = paragraph.MarkRevision is not null;
        if (paragraph.MarkRevision is { } revision)
        {
            RevisionWords(revision);
        }

        if (end is not null)
        {
            Word(end);
            if (marked)
            {
                // A tracked paragraph mark's state lasts past the mark; the next paragraph starts without it.
                Word(paragraph.MarkRevision!.Kind is RevisionKind.Deletion or RevisionKind.MoveFrom ? "deleted" : "revised", 0);
            }
        }

        output.AppendLine();
        current = null;
    }

    private Paragraph? current;
    private int currentLevel;

    /// <summary>The paragraph's own words: <c>\pard\plain</c>, its table level, style, formatting and formatting change.</summary>
    private void ParagraphProperties(Paragraph paragraph, int level)
    {
        Word("pard");
        Word("plain");
        if (level > 0)
        {
            Word("intbl");
            if (level > 1)
            {
                Word("itap", level);
            }
        }

        ParagraphStyle? style = paragraph.Style is { IsInDocument: true } own ? own : document.Styles.DefaultParagraphStyle;
        Word("s", StyleIndex(style));
        ParagraphWords(FormatOf(paragraph));
        if (paragraph.FormatRevision is { OldFormat: ParagraphFormat old } change)
        {
            Word("prauth", Author(change.Author));
            if (Dttm(change.Date) is int date)
            {
                Word("prdate", date);
            }

            Destination("oldpprops");
            ParagraphWords(old);
            Close();
        }
    }

    /// <summary>
    /// The paragraph formatting RTF writes: what the styles give with the direct formatting over it. In a table with a style,
    /// the paragraph's whole effective formatting, the table style's included: RTF has no conditional table formatting
    /// (a bold first row, banded shading), so what it gives is written on the text it applies to.
    /// </summary>
    private ParagraphFormat FormatOf(Paragraph paragraph)
    {
        if (InStyledTable(paragraph))
        {
            ResolvedParagraphFormat resolved = paragraph.GetEffectiveFormat();
            ParagraphFormat full = new();
            for (int id = 0; id < ParagraphFormat.Count; id++)
            {
                full.SetRawSilently(id, resolved.GetRaw(id));
            }

            return full;
        }

        return Effective(given.Paragraph(paragraph.Style, paragraph.Format), paragraph.Format);
    }

    /// <summary>The character formatting RTF writes for a run, as <see cref="FormatOf(Paragraph)"/> says for paragraphs.</summary>
    private RunFormat FormatOf(Run run)
    {
        if (current is { } paragraph && InStyledTable(paragraph))
        {
            ResolvedRunFormat resolved = FormatResolver.ResolveRunIn(run, paragraph);
            RunFormat full = new();
            for (int id = 0; id < RunFormat.Count; id++)
            {
                full.SetRawSilently(id, resolved.GetRaw(id));
            }

            return full;
        }

        return Effective(given.Run(current?.Style, run.Style), run.Format);
    }

    private static bool InStyledTable(Paragraph paragraph)
    {
        return paragraph.Parent is Cell { Row.Table.Style.IsInDocument: true };
    }

    /// <summary>What the styles give with the direct formatting over it: the formatting RTF writes.</summary>
    private static T Effective<T>(T styles, T direct)
        where T : FormatBase, new()
    {
        if (direct.IsEmpty)
        {
            return styles;
        }

        T result = new();
        RtfStyleFormats.Merge(result, styles);
        RtfStyleFormats.Merge(result, direct);
        return result;
    }

    // ---- Writing ----

    private void Open()
    {
        output.Append('{');
        pendingSpace = false;
    }

    private void Close()
    {
        output.Append('}');
        pendingSpace = false;
    }

    /// <summary>Opens an ignorable destination's group: <c>{\*\word</c>.</summary>
    private void Destination(string word)
    {
        output.Append('{').Append("\\*\\").Append(word);
        pendingSpace = true;
    }

    private void Word(string word)
    {
        output.Append('\\').Append(word);
        pendingSpace = true;
    }

    private void Word(string word, long value)
    {
        output.Append('\\').Append(word).Append(value.ToString(CultureInfo.InvariantCulture));
        pendingSpace = true;
    }

    /// <summary>A toggle word: bare when on, with 0 when off.</summary>
    private void Toggle(string word, bool on)
    {
        if (on)
        {
            Word(word);
        }
        else
        {
            Word(word, 0);
        }
    }

    /// <summary>A space that ends the control word before it, when the text after it should not start with one.</summary>
    private void Space()
    {
        output.Append(' ');
        pendingSpace = false;
    }

    /// <summary>Raw RTF kept from a file read, written back as it was.</summary>
    private void Raw(string markup)
    {
        output.Append(markup);
        pendingSpace = false;
    }

    /// <summary>
    /// Text, escaped: braces and backslashes as control symbols, tabs as <c>\tab</c>, the non-breaking space and hyphens as
    /// their symbols, and every character outside printable ASCII as <c>\uN</c> with a one-character fallback for readers
    /// without Unicode (<c>\uc1</c>). A character past U+FFFF is its two surrogates, each escaped; half a pair is U+FFFD.
    /// </summary>
    private void Text(ReadOnlySpan<char> text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is >= ' ' and <= '~' and not ('\\' or '{' or '}'))
            {
                continue;
            }

            Plain(text[start..i]);
            start = i + 1;
            switch (c)
            {
                case '\\' or '{' or '}':
                    output.Append('\\').Append(c);
                    pendingSpace = false;
                    break;
                case '\t':
                    Word("tab");
                    break;
                case '\u00A0':
                    output.Append("\\~");
                    pendingSpace = false;
                    break;
                case '\u00AD':
                    output.Append("\\-");
                    pendingSpace = false;
                    break;
                case '\u2011':
                    output.Append("\\_");
                    pendingSpace = false;
                    break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        Unicode(c);
                        Unicode(text[i + 1]);
                        i++;
                        start = i + 1;
                    }
                    else
                    {
                        Unicode(char.IsSurrogate(c) ? '\uFFFD' : c);
                    }

                    break;
            }
        }

        Plain(text[start..]);
    }

    /// <summary>Printable ASCII other than braces and backslashes, with the space a control word before it needs.</summary>
    private void Plain(ReadOnlySpan<char> text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (pendingSpace && (char.IsAsciiLetterOrDigit(text[0]) || text[0] is ' ' or '-'))
        {
            output.Append(' ');
        }

        output.Append(text);
        pendingSpace = false;
    }

    /// <summary>One UTF-16 unit as <c>\uN</c> (signed, as RTF's parameters are) and its fallback: the Windows-1252 byte, or a question mark.</summary>
    private void Unicode(char c)
    {
        output.Append("\\u").Append(((short)c).ToString(CultureInfo.InvariantCulture));
        if (Windows1252(c) is byte b)
        {
            output.Append("\\'").Append(HexDigits[b >> 4]).Append(HexDigits[b & 0xF]);
        }
        else
        {
            output.Append('?');
        }

        pendingSpace = false;
    }

    private const string HexDigits = "0123456789abcdef";

    /// <summary>The Windows-1252 byte of a character, or null when the code page has none.</summary>
    private static byte? Windows1252(char c)
    {
        if (c is >= '\u00A0' and <= '\u00FF')
        {
            return (byte)c;
        }

        if (c < '\u0100')
        {
            return null;
        }

        int at = Windows1252High.IndexOf(c);
        return at >= 0 ? (byte)(0x80 + at) : null;
    }

    /// <summary>The characters of Windows-1252's bytes 0x80 to 0x9F, a NUL where a byte has none.</summary>
    private const string Windows1252High =
        "\u20AC\0\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152\0\u017D\0"
        + "\0\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\0\u017E\u0178";

    /// <summary>A date as Word's DTTM (minutes, hours, day, month and years since 1900 in 32 bits), in UTC; null for none.</summary>
    private static int? Dttm(DateTimeOffset? date)
    {
        if (date is not { } value)
        {
            return null;
        }

        DateTime utc = value.UtcDateTime;
        if (utc.Year is < 1900 or > 2411)
        {
            return null;
        }

        uint packed = (uint)utc.Minute | ((uint)utc.Hour << 6) | ((uint)utc.Day << 11) | ((uint)utc.Month << 16) | ((uint)(utc.Year - 1900) << 20);
        return unchecked((int)packed);
    }

    /// <summary>Points as twips, rounded and kept to RTF's parameter range.</summary>
    private static long Twips(double points)
    {
        double twips = Math.Round(points * 20);
        return double.IsFinite(twips) ? (long)Math.Clamp(twips, -1_073_741_823, 1_073_741_823) : 0;
    }

    private static long Clamp(double value)
    {
        double rounded = Math.Round(value);
        return double.IsFinite(rounded) ? (long)Math.Clamp(rounded, -1_073_741_823, 1_073_741_823) : 0;
    }
}
