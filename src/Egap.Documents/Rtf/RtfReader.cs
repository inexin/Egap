using System.Runtime.CompilerServices;
using System.Text;

namespace Egap.Documents.Rtf;

/// <summary>
/// Reads RTF into the model (RTF 1.9.1): a tokenizer pass that keeps RTF's group-scoped character and paragraph state and
/// maps each control word onto the model as it comes. The header tables (fonts, colours, styles, lists, information) are
/// read first; the body builds paragraphs, tables and sections, and destinations such as notes, headers, comments, fields
/// and text boxes are read as stories of their own. Groups marked <c>\*</c> that the reader does not know are kept as
/// extension data (<see cref="RtfExtension"/>).
/// </summary>
internal sealed partial class RtfReader
{
    private readonly RtfTokenizer tokens;
    private readonly RtfLoadOptions options;
    private readonly Document document = new(blank: true);
    private readonly Dictionary<(ConversionWarningKind Kind, string Message), int> warnings = [];
    private readonly List<State> states = [];
    private readonly StringBuilder text = new();
    private byte[] bytes = new byte[64];
    private int byteCount;
    /// <summary>
    /// The most entries the reader takes into any one table or collection it builds (styles, lists, fonts, colours,
    /// authors, bookmarks, comments): far more than any real file has, and few enough that a hostile file stays fast.
    /// </summary>
    private const int MaxEntries = 10_000;

    private int depth;
    private int nesting;
    private RtfStyleFormats.Cache? given;
    private readonly List<Run> surrogateRuns = [];
    /// <summary>
    /// The fallback characters still to skip after a <c>\uN</c>. A group's start or end ends the fallback, so
    /// <see cref="Push"/> and <see cref="Pop"/> clear it, whichever loop is reading the group.
    /// </summary>
    private int skip;
    private State state = null!;
    private StoryContext story = null!;
    private readonly StoryContext main;
    private Section section;

    private RtfReader(byte[] data, RtfLoadOptions options)
    {
        tokens = new RtfTokenizer(data);
        this.options = options;
        section = document.Sections.AddTrusted(new Section(document));
        main = new StoryContext(section.Body.Blocks, isMain: true);
        story = main;
        document.Settings.CompatibilityMode = 15;
        document.Styles.DefaultRunFormat.Size = 12;
    }

    public static Document Read(byte[] data, RtfLoadOptions options, out IReadOnlyList<ConversionWarning> warnings)
    {
        RtfReader reader = new(data, options);
        try
        {
            reader.ReadDocument();
        }
        catch (Exception e) when (e is not (DocumentException or OutOfMemoryException))
        {
            // Malformed input must not escape as an arbitrary exception: callers catch DocumentException for a bad file.
            throw new DocumentException("The RTF file is malformed: " + e.Message, e);
        }

        warnings = [.. reader.warnings.Select(w => new ConversionWarning(w.Key.Kind, w.Key.Message, w.Value))];
        return reader.document;
    }

    private void ReadDocument()
    {
        // RTF starts with "{\rtf"; a byte order mark or white space before it is let through, as Word does.
        if (tokens.Next() != RtfTokenKind.GroupStart || tokens.Next() != RtfTokenKind.ControlWord || tokens.Word != "rtf")
        {
            throw new DocumentException("The file is not RTF: it does not start with {\\rtf.");
        }

        states.Add(new State());
        depth = 0;
        state = states[0];
        Push(0);
        ReadGroup(1, main);
        JoinSurrogates();
        if (!main.Finished)
        {
            FinishStory(main);
        }

        FinishSection();
        FinishHeader();
        FinishProtection();
        if (document.Sections.Count > 1 && document.Sections[^1].Body.Blocks.Count == 0)
        {
            // A last \sect with nothing after it closes the document's last section; it adds no empty one.
            document.Sections[^1].Remove();
        }
    }

    /// <summary>
    /// Reads tokens until the group opened at <paramref name="groupDepth"/> closes (or the file ends), handling nested groups
    /// and the destinations they start.
    /// </summary>
    /// <remarks>
    /// When <paramref name="finish"/> is given, that story is finished before the group's closing brace, while the group's
    /// state still holds its last paragraph's formatting.
    /// </remarks>
    private void ReadGroup(int groupDepth, StoryContext? finish = null)
    {
        Enter();
        try
        {
            ReadGroupTokens(groupDepth, finish);
        }
        finally
        {
            nesting--;
        }
    }

    private void ReadGroupTokens(int groupDepth, StoryContext? finish)
    {
        while (true)
        {
            RtfTokenKind kind = tokens.Next();
            if (skip > 0 && kind is RtfTokenKind.Text or RtfTokenKind.Hex or RtfTokenKind.ControlWord or RtfTokenKind.ControlSymbol)
            {
                if (kind == RtfTokenKind.Text && tokens.Text.Length > skip)
                {
                    int count = skip;
                    skip = 0;
                    AppendText(tokens.Text[count..]);
                    continue;
                }

                skip = kind == RtfTokenKind.Text ? skip - tokens.Text.Length : skip - 1;
                skip = Math.Max(skip, 0);
                if (kind == RtfTokenKind.ControlWord && tokens.Word == "bin" && tokens.HasParameter)
                {
                    _ = tokens.ReadBinary(tokens.Parameter);
                }

                continue;
            }

            if (state.Ignorable && (kind is RtfTokenKind.Text or RtfTokenKind.Hex or RtfTokenKind.GroupStart || kind == RtfTokenKind.ControlSymbol && tokens.Symbol != '*'))
            {
                // \* names no destination word here, so the group is one the reader does not know: kept whole, and none of
                // its text leaks into the document.
                state.Ignorable = false;
                if (kind == RtfTokenKind.GroupStart)
                {
                    _ = tokens.SkipGroup();
                }

                SkipDestination(keep: true);
                if (depth < groupDepth)
                {
                    return;
                }

                continue;
            }

            switch (kind)
            {
                case RtfTokenKind.End:
                    FlushText();
                    if (finish is not null)
                    {
                        FinishStory(finish);
                    }

                    while (depth >= groupDepth)
                    {
                        Pop();
                    }

                    return;
                case RtfTokenKind.GroupStart:
                    FlushText();
                    Push(tokens.Position - 1);
                    break;
                case RtfTokenKind.GroupEnd:
                    FlushText();
                    if (finish is not null && depth == groupDepth)
                    {
                        FinishStory(finish);
                    }

                    Pop();
                    if (depth < groupDepth)
                    {
                        return;
                    }

                    break;
                case RtfTokenKind.Text:
                    AppendText(tokens.Text);
                    break;
                case RtfTokenKind.Hex:
                    AppendByte(tokens.Byte);
                    break;
                case RtfTokenKind.ControlSymbol:
                    Symbol(tokens.Symbol);
                    break;
                case RtfTokenKind.ControlWord:
                    if (ControlWord(tokens.Word, tokens.HasParameter, tokens.Parameter) && depth < groupDepth)
                    {
                        // A destination read its whole group, the one this call was reading.
                        return;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Counts one more level of reading that can recurse, a nested group read or a destination, which need not open a group
    /// of its own (<c>\fldinst\fldinst...</c>). Past <see cref="DocumentLimits.MaxDepth"/>, or near the end of the stack,
    /// the file is refused rather than overflowing the stack.
    /// </summary>
    private void Enter()
    {
        if (++nesting > options.Limits.MaxDepth || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            nesting--;
            throw new DocumentLimitException($"RTF destinations nest deeper than {options.Limits.MaxDepth}.");
        }
    }

    /// <summary>Opens a group: its state starts as a copy of the enclosing one.</summary>
    private void Push(int start)
    {
        if (depth + 1 > options.Limits.MaxDepth || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new DocumentLimitException($"RTF groups nest deeper than {options.Limits.MaxDepth}.");
        }

        skip = 0;
        depth++;
        if (states.Count <= depth)
        {
            states.Add(new State());
        }

        State next = states[depth];
        next.CopyFrom(state);
        next.GroupStart = start;
        state = next;
    }

    /// <summary>Closes a group, going back to the enclosing state.</summary>
    private void Pop()
    {
        skip = 0;
        if (depth > 0)
        {
            depth--;
        }

        state = states[depth];
    }

    /// <summary>
    /// Skips the rest of the current group and closes it. A group that was marked <c>\*</c> and is not one the reader knows
    /// is kept, raw, as extension data where it sat.
    /// </summary>
    private void SkipDestination(bool keep = false)
    {
        int start = state.GroupStart;
        int end = tokens.SkipGroup();
        if (keep && start >= 0)
        {
            string markup = Encoding.Latin1.GetString(tokens.Data, start, Math.Min(end + 1, tokens.Data.Length) - start);
            if (tokens.Unclosed > 0)
            {
                // A group cut off by the end of the file is closed, so that writing it back cannot swallow what follows.
                markup = markup.TrimEnd('\\') + new string('}', tokens.Unclosed);
            }

            Keep(markup);
        }

        Pop();
    }

    /// <summary>Whether the control symbol just read follows its group's brace with nothing but line breaks between.</summary>
    private bool AtGroupStart()
    {
        int start = state.GroupStart;
        int symbol = tokens.Position - 2;
        if (start < 0 || symbol <= start)
        {
            return false;
        }

        return !tokens.Data.AsSpan(start + 1, symbol - start - 1).ContainsAnyExcept((byte)'\r', (byte)'\n');
    }

    /// <summary>Skips the rest of a group whose first token after the brace was <paramref name="first"/>.</summary>
    private void SkipRest(RtfTokenKind first)
    {
        switch (first)
        {
            case RtfTokenKind.GroupEnd:
                Pop();
                return;
            case RtfTokenKind.GroupStart:
                Push(tokens.Position - 1);
                SkipDestination();
                SkipDestination();
                return;
            case RtfTokenKind.End:
                return;
            default:
                SkipDestination();
                return;
        }
    }

    private void Symbol(char symbol)
    {
        switch (symbol)
        {
            case '*':
                // \* marks the group it opens; one later in a group marks nothing.
                state.Ignorable = AtGroupStart();
                break;
            case '\\' or '{' or '}':
                AppendChar(symbol);
                break;
            case '~':
                AppendChar(' ');
                break;
            case '-':
                AppendChar('­');
                break;
            case '_':
                AppendChar('‑');
                break;
            case '\t':
                AddInline(new Tab(document));
                break;
            case ':':
                // An index subentry separator, only meaningful inside index entries.
                break;
        }
    }

    // ---- Text ----

    private void AppendText(ReadOnlySpan<byte> value)
    {
        while (value.Length > 0)
        {
            int tab = value.IndexOf((byte)'\t');
            ReadOnlySpan<byte> part = tab < 0 ? value : value[..tab];
            if (byteCount == 0 && !part.ContainsAnyExceptInRange((byte)0x20, (byte)0x7E))
            {
                foreach (byte b in part)
                {
                    text.Append((char)b);
                }
            }
            else
            {
                foreach (byte b in part)
                {
                    AppendByte(b);
                }
            }

            if (tab < 0)
            {
                break;
            }

            AddInline(new Tab(document));
            value = value[(tab + 1)..];
        }
    }

    private void AppendByte(byte value)
    {
        if (byteCount == bytes.Length)
        {
            Array.Resize(ref bytes, bytes.Length * 2);
        }

        bytes[byteCount++] = value;
    }

    private void AppendChar(char value)
    {
        FlushBytes();
        text.Append(value);
    }

    /// <summary>Decodes the bytes gathered so far in the current font's code page.</summary>
    private void FlushBytes()
    {
        if (byteCount == 0)
        {
            return;
        }

        text.Append(EncodingOf(state.Font).GetString(bytes, 0, byteCount));
        byteCount = 0;
    }

    /// <summary>Turns the text gathered so far into a run with the current character formatting.</summary>
    private void FlushText()
    {
        FlushBytes();
        if (text.Length == 0)
        {
            return;
        }

        string value = text.ToString();
        text.Clear();
        AddInline(NewRun(value));
    }

    private Run NewRun(string value)
    {
        FinishHeader();
        Run run = new(document, value);
        if (value.AsSpan().ContainsAnyInRange('\uD800', '\uDFFF'))
        {
            surrogateRuns.Add(run);
        }

        if (!state.Char.IsEmpty)
        {
            run.Format.CopyRaw(state.Char);
        }

        if (state.CharacterStyle >= 0 && CharacterStyleAt(state.CharacterStyle) is { } style)
        {
            run.Style = style;
        }

        // The run repeats what its styles give (RTF has no inheritance in the text); only what differs is its own.
        RtfStyleFormats.Subtract(run.Format, Given.Run(ParagraphStyleAt(state.ParagraphStyle), run.Style));

        if (state.CharChange is { } change)
        {
            run.FormatRevision = new FormatRevision(Author(change.Author), change.Date, change.Old ?? new RunFormat());
        }

        return run;
    }

    /// <summary>
    /// Joins surrogate pairs that a brace split over two runs (<c>\uN{}\uM{}</c>, as <see cref="RtfFormat"/>.Parse
    /// writes a character past U+FFFF), and replaces a surrogate that has no partner with U+FFFD, so no run holds half a
    /// character.
    /// </summary>
    private void JoinSurrogates()
    {
        foreach (Run run in surrogateRuns)
        {
            if (run.Text is [.., char high] && char.IsHighSurrogate(high) && run.NextSibling is Run { Text: [char low, ..] } next
                && char.IsLowSurrogate(low))
            {
                run.Text += low;
                next.Text = next.Text[1..];
                if (next.Text.Length == 0)
                {
                    next.Remove();
                }
            }

            run.Text = WithoutLoneSurrogates(run.Text);
        }
    }

    /// <summary>The text with each surrogate that is not half of a pair replaced by U+FFFD.</summary>
    private static string WithoutLoneSurrogates(string value)
    {
        if (!value.AsSpan().ContainsAnyInRange('\uD800', '\uDFFF'))
        {
            return value;
        }

        char[] chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(chars[i]))
            {
                chars[i] = '\uFFFD';
            }
        }

        return new string(chars);
    }

    /// <summary>What the styles give, once the header (and so every style) is read.</summary>
    private RtfStyleFormats.Cache Given => given ??= new RtfStyleFormats.Cache(document);

    /// <summary>Adds an inline to the paragraph being read, starting one when there is none.</summary>
    private T AddInline<T>(T inline)
        where T : Inline
    {
        FlushText();
        if (RevisionOf(state) is { } revision)
        {
            inline.Revision = revision;
        }

        return EnsureParagraph().Inlines.AddTrusted(inline);
    }

    private Paragraph EnsureParagraph()
    {
        FinishHeader();
        return story.Paragraph ??= new Paragraph(document);
    }

    // ---- Paragraphs, stories and sections ----

    /// <summary>The table level the current paragraph belongs to: 0 outside tables.</summary>
    private int Level => state.InTable || state.Itap > 0 ? Math.Max(1, state.Itap) : 0;

    /// <summary>Ends the paragraph being read with the current paragraph formatting, at table level <paramref name="level"/>.</summary>
    private void EndParagraph(int level, bool force = true)
    {
        FlushText();
        if (story.Paragraph is null && !force)
        {
            return;
        }

        Paragraph paragraph = EnsureParagraph();
        story.Paragraph = null;
        ApplyParagraph(paragraph);
        Container(level).AddTrusted(paragraph);
    }

    private void ApplyParagraph(Paragraph paragraph)
    {
        if (!state.Para.IsEmpty)
        {
            paragraph.Format.CopyRaw(state.Para);
        }

        if (ParagraphStyleAt(state.ParagraphStyle) is { } style)
        {
            paragraph.Style = style;
        }

        if (state.ListOverride > 0 && lists.TryGetValue(state.ListOverride, out List? list))
        {
            paragraph.Format.List = list;
            paragraph.Format.ListLevel = Math.Clamp(state.ListLevel, 0, 8);
        }
        else if (state.ListOverride == 0 && state.Pn is { } pn && !pn.Ignored && ListOf(pn) is { } pnList)
        {
            paragraph.Format.List = pnList;
            paragraph.Format.ListLevel = 0;
        }

        if (!state.Char.IsEmpty)
        {
            paragraph.MarkFormat.CopyRaw(state.Char);
        }

        // The paragraph repeats what its style and list give; only what differs is its own.
        FinishHeader();
        RtfStyleFormats.Subtract(paragraph.Format, Given.Paragraph(paragraph.Style, paragraph.Format));
        RtfStyleFormats.Subtract(paragraph.MarkFormat, Given.Run(paragraph.Style, null, mark: true));
        if (RevisionOf(state) is { } revision)
        {
            paragraph.MarkRevision = revision;
        }

        if (state.ParaChange is { } change)
        {
            paragraph.FormatRevision = new FormatRevision(Author(change.Author), change.Date, change.Old ?? new ParagraphFormat());
        }
    }

    /// <summary>Ends the story being read: its last paragraph, if it has text, and its tables.</summary>
    private void FinishStory(StoryContext context)
    {
        FlushText();
        if (context.Paragraph is { } paragraph && (paragraph.Inlines.Count > 0 || paragraph.HasExtensions || context.Blocks.Count == 0))
        {
            EndParagraph(0);
        }

        context.Paragraph = null;
        CloseTablesAbove(0);
        context.Finished = true;
        if (context.Blocks.Count == 0 && !context.IsMain)
        {
            Paragraph empty = new(document);
            ApplyParagraph(empty);
            context.Blocks.AddTrusted(empty);
        }
    }

    /// <summary>Reads a destination's group as a story of its own, into <paramref name="blocks"/>.</summary>
    private void ReadStory(NodeCollection<Block> blocks)
    {
        FlushText();
        StoryContext outer = story;
        int groupDepth = depth;
        story = new StoryContext(blocks, isMain: false);
        try
        {
            // A story starts with the default paragraph and character formatting, whatever surrounds its reference.
            state.ResetParagraph();
            state.ResetCharacter();
            ReadGroup(groupDepth, story);
            if (!story.Finished)
            {
                // A destination word in the middle of the group read the rest of it.
                FinishStory(story);
            }
        }
        finally
        {
            story = outer;
        }
    }

    /// <summary>Ends a section: its last paragraph, its page setup, and a new section after it.</summary>
    private void EndSection()
    {
        if (!story.IsMain)
        {
            // A section break inside a note or a header is only a paragraph mark.
            EndParagraph(Level);
            return;
        }

        EndParagraph(0);
        CloseTablesAbove(0);
        FinishSection();
        section = document.Sections.AddTrusted(new Section(document));
        main.Blocks = section.Body.Blocks;
    }

    private void FinishSection()
    {
        section.PageSetup.CopyRaw(sectionSetup);
        if (sectionPageRestart)
        {
            section.PageSetup.PageNumberStart = sectionPageStart ?? 1;
        }
    }

    private void Warn(ConversionWarningKind kind, string message)
    {
        warnings[(kind, message)] = warnings.GetValueOrDefault((kind, message)) + 1;
    }

    /// <summary>Keeps a group the reader does not know where it sat: before the body, or in the paragraph being read.</summary>
    private void Keep(string markup)
    {
        if (story.IsMain && !bodyStarted)
        {
            (RtfExtension.Of(document).Groups ??= []).Add(markup);
            return;
        }

        Paragraph paragraph = EnsureParagraph();
        (RtfExtension.Of(paragraph).Inline ??= []).Add((paragraph.Inlines.Count, markup));
    }

    /// <summary>Where the blocks of the story being read go.</summary>
    private sealed class StoryContext(NodeCollection<Block> blocks, bool isMain)
    {
        public NodeCollection<Block> Blocks { get; set; } = blocks;

        public bool IsMain { get; } = isMain;

        /// <summary>The paragraph being read, until its paragraph mark.</summary>
        public Paragraph? Paragraph { get; set; }

        /// <summary>Whether the story's last paragraph and tables are done.</summary>
        public bool Finished { get; set; }

        /// <summary>The tables being built, by nesting level from 1.</summary>
        public List<TableLevel> Tables { get; } = [];
    }
}
