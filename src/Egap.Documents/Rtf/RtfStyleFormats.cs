namespace Egap.Documents.Rtf;

/// <summary>
/// The formatting styles give, as RTF spells it out. RTF text carries its whole formatting: a paragraph or run written in a
/// style repeats the style's formatting, and a style in the style sheet repeats what it inherits (as Word writes both). The
/// writer writes what the styles give together with the direct formatting; the reader takes what the styles give away again,
/// so that only what differs from the styles is direct formatting, as in a DOCX file of the same document.
/// </summary>
internal static class RtfStyleFormats
{
    /// <summary>
    /// What a paragraph's styles give its paragraph formatting: the document defaults, the style and its bases, and the level
    /// of <paramref name="list"/> it is in (Word's order). Tab stops add up through the levels.
    /// </summary>
    public static ParagraphFormat Paragraph(Document document, ParagraphStyle? style, List? list, int level)
    {
        ParagraphFormat given = new();
        Merge(given, document.Styles.DefaultParagraphFormat);
        foreach (ParagraphStyle s in Chain(style))
        {
            Merge(given, s.ParagraphFormat);
        }

        if (list is not null && list != List.None && document.Lists.Contains(list))
        {
            Merge(given, list.GetLevel(Math.Clamp(level, 0, 8)).ParagraphFormat);
        }

        return given;
    }

    /// <summary>
    /// What a run's styles give its character formatting: the document defaults, the paragraph style's chain, then the
    /// character style's. Each kind of style gives a toggle property (bold, italic...) the value nearest in its chain, and the
    /// two kinds combine by exclusive or, as Word combines them.
    /// </summary>
    public static RunFormat Run(Document document, ParagraphStyle? paragraphStyle, CharacterStyle? characterStyle)
    {
        RunFormat given = new();
        List<ParagraphStyle> paragraph = Chain(paragraphStyle);
        List<CharacterStyle> character = Chain(characterStyle);
        Merge(given, document.Styles.DefaultRunFormat);
        foreach (ParagraphStyle s in paragraph)
        {
            Merge(given, s.RunFormat);
        }

        foreach (CharacterStyle s in character)
        {
            Merge(given, s.RunFormat);
        }

        foreach (int id in RunFormat.Toggles)
        {
            bool? fromParagraph = null;
            foreach (ParagraphStyle s in paragraph)
            {
                fromParagraph = s.RunFormat.GetRaw(id) as bool? ?? fromParagraph;
            }

            bool? fromCharacter = null;
            foreach (CharacterStyle s in character)
            {
                fromCharacter = s.RunFormat.GetRaw(id) as bool? ?? fromCharacter;
            }

            bool? value = fromParagraph is null && fromCharacter is null
                ? document.Styles.DefaultRunFormat.GetRaw(id) as bool?
                : (fromParagraph ?? false) ^ (fromCharacter ?? false);
            given.SetRawSilently(id, value);
        }

        return given;
    }

    /// <summary>What a style inherits: its base's whole formatting, or, for a paragraph style with no base, the document defaults.</summary>
    public static (ParagraphFormat Paragraph, RunFormat Run) Inherited(Style style)
    {
        ParagraphFormat paragraph = new();
        RunFormat run = new();
        if (style is ParagraphStyle)
        {
            Merge(paragraph, style.Document.Styles.DefaultParagraphFormat);
            Merge(run, style.Document.Styles.DefaultRunFormat);
        }

        if (style is ParagraphStyle or CharacterStyle)
        {
            foreach (Style s in ChainOf(style.BasedOn))
            {
                if (s.GetType() == style.GetType())
                {
                    Merge(paragraph, (s as ParagraphStyle)?.ParagraphFormat ?? new ParagraphFormat());
                    Merge(run, s is ParagraphStyle p ? p.RunFormat : ((CharacterStyle)s).RunFormat);
                }
            }
        }

        return (paragraph, run);
    }

    /// <summary>Sets each property <paramref name="level"/> sets; tab stops add up (a clear stop removes one), a font slot's name and theme font go together.</summary>
    public static void Merge(FormatBase target, FormatBase level)
    {
        for (int i = 0; i < target.PropertyCount; i++)
        {
            if (level.GetRaw(i) is not { } value)
            {
                continue;
            }

            if (target is ParagraphFormat paragraph && i == ParagraphFormat.TabStopsId)
            {
                paragraph.TabStops = AddTabs(paragraph.TabStops, (IReadOnlyList<TabStop>)value);
            }
            else if (target is RunFormat && IsFontSlot(i))
            {
                int slot = (i - RunFormat.FontAsciiId) % 4;
                target.SetRawSilently(RunFormat.FontAsciiId + slot, level.GetRaw(RunFormat.FontAsciiId + slot));
                target.SetRawSilently(RunFormat.ThemeFontAsciiId + slot, level.GetRaw(RunFormat.ThemeFontAsciiId + slot));
            }
            else
            {
                target.SetRawSilently(i, value);
            }
        }
    }

    /// <summary>
    /// Clears, without notifying, each property of <paramref name="direct"/> that has the value <paramref name="given"/> gives. Tab stops become
    /// what changes the given ones: the stops added, and a clear stop for each given stop that is gone.
    /// </summary>
    public static void Subtract(FormatBase direct, FormatBase given)
    {
        if (direct.IsEmpty)
        {
            return;
        }

        for (int i = 0; i < direct.PropertyCount; i++)
        {
            object? value = direct.GetRaw(i);
            if (value is null)
            {
                continue;
            }

            if (direct is ParagraphFormat && i == ParagraphFormat.TabStopsId)
            {
                direct.SetRawSilently(i, TabChanges((IReadOnlyList<TabStop>)value, (given as ParagraphFormat)?.TabStops ?? []));
            }
            else if (Equals(value, given.GetRaw(i)))
            {
                direct.SetRawSilently(i, null);
            }
        }
    }

    /// <summary>The tab stops after <paramref name="added"/> changes <paramref name="stops"/>, by position.</summary>
    public static TabStop[] AddTabs(IReadOnlyList<TabStop>? stops, IReadOnlyList<TabStop> added)
    {
        List<TabStop> result = [.. stops ?? []];
        foreach (TabStop stop in added)
        {
            result.RemoveAll(t => Math.Abs(t.Position - stop.Position) < 0.01);
            if (stop.Alignment != TabAlignment.Clear)
            {
                result.Add(stop);
            }
        }

        result.Sort((a, b) => a.Position.CompareTo(b.Position));
        return [.. result];
    }

    /// <summary>The changes that turn <paramref name="given"/> into <paramref name="all"/>, by position; null for none.</summary>
    private static TabStop[]? TabChanges(IReadOnlyList<TabStop> all, IReadOnlyList<TabStop> given)
    {
        List<TabStop> changes = [];
        foreach (TabStop stop in all)
        {
            if (stop.Alignment != TabAlignment.Clear && !given.Contains(stop))
            {
                changes.Add(stop);
            }
        }

        foreach (TabStop stop in given)
        {
            if (!all.Any(t => Math.Abs(t.Position - stop.Position) < 0.01 && t.Alignment != TabAlignment.Clear))
            {
                changes.Add(new TabStop(stop.Position, TabAlignment.Clear));
            }
        }

        changes.Sort((a, b) => a.Position.CompareTo(b.Position));
        return changes.Count == 0 ? null : [.. changes];
    }

    /// <summary>What the styles give, kept per style, list and level while one file is read or written.</summary>
    public sealed class Cache(Document document)
    {
        private readonly Dictionary<(ParagraphStyle?, List?, int), ParagraphFormat> paragraphs = [];
        private readonly Dictionary<(ParagraphStyle?, CharacterStyle?), RunFormat> runs = [];

        /// <summary>
        /// What <paramref name="style"/> (the default paragraph style for null) gives a paragraph whose own formatting is
        /// <paramref name="direct"/>: the list level counts from the list the paragraph or its style is in.
        /// </summary>
        public ParagraphFormat Paragraph(ParagraphStyle? style, ParagraphFormat direct)
        {
            style = Effective(style) ?? Effective(document.Styles.DefaultParagraphStyle);
            ParagraphFormat styleOnly = Get(style, null, 0);
            List? list = direct.List ?? styleOnly.List;
            return list is null ? styleOnly : Get(style, list, direct.ListLevel ?? styleOnly.ListLevel ?? 0);
        }

        /// <summary>What the styles give a run: <paramref name="character"/> null for a paragraph mark, which has no character style.</summary>
        public RunFormat Run(ParagraphStyle? paragraph, CharacterStyle? character, bool mark = false)
        {
            paragraph = Effective(paragraph) ?? Effective(document.Styles.DefaultParagraphStyle);
            character = mark ? null : Effective(character) ?? Effective(document.Styles.DefaultCharacterStyle);
            if (!runs.TryGetValue((paragraph, character), out RunFormat? given))
            {
                given = RtfStyleFormats.Run(document, paragraph, character);
                runs[(paragraph, character)] = given;
            }

            return given;
        }

        private ParagraphFormat Get(ParagraphStyle? style, List? list, int level)
        {
            if (!paragraphs.TryGetValue((style, list, level), out ParagraphFormat? given))
            {
                given = RtfStyleFormats.Paragraph(document, style, list, level);
                paragraphs[(style, list, level)] = given;
            }

            return given;
        }

        private static T? Effective<T>(T? style)
            where T : Style
        {
            return style is { IsInDocument: true } ? style : null;
        }
    }

    private static bool IsFontSlot(int id)
    {
        return id is >= RunFormat.FontAsciiId and <= RunFormat.FontComplexScriptId
            or >= RunFormat.ThemeFontAsciiId and <= RunFormat.ThemeFontComplexScriptId;
    }

    /// <summary>A style and its bases, base first, leaving out styles not in the document.</summary>
    private static List<T> Chain<T>(T? style)
        where T : Style
    {
        return [.. ChainOf(style).OfType<T>()];
    }

    private static List<Style> ChainOf(Style? style)
    {
        List<Style> chain = [];
        for (Style? s = style; s is not null && chain.Count < 100; s = s.BasedOn)
        {
            if (s.IsInDocument && !chain.Contains(s))
            {
                chain.Add(s);
            }
        }

        chain.Reverse();
        return chain;
    }
}
