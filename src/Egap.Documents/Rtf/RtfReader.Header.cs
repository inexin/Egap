using System.Text;

using Egami.Pixels;

namespace Egap.Documents.Rtf;

/// <summary>The header's tables: fonts, colours, styles, lists and list overrides, revision authors and document information.</summary>
internal sealed partial class RtfReader
{
    private readonly Dictionary<int, FontEntry> fonts = [];
    private readonly Dictionary<int, Encoding> encodings = [];
    private readonly List<DocumentColor> colors = [];
    private readonly Dictionary<int, Style> styles = [];
    private readonly Dictionary<int, StyleEntry> madeFrom = [];
    private readonly List<StyleEntry> styleEntries = [];
    private readonly Dictionary<int, ListDefinition> listDefinitions = [];
    private readonly Dictionary<int, List> lists = [];
    private readonly Dictionary<PnState, List> pnLists = [];
    private readonly Dictionary<string, int> styleIdSuffixes = new(StringComparer.OrdinalIgnoreCase);
    private int nextPnListId = 1;
    private readonly List<string> authors = [];
    private readonly int[] defaultFonts = [-1, -1, -1, -1];
    private int ansiCodePage = 1252;
    private int defaultFont = -1;
    private bool bodyStarted;

    /// <summary>A font table entry: the name, the character set its text is in, and Word's hints for substituting it.</summary>
    private sealed record FontEntry(string Name, int Charset, int CodePage, string? AltName, string? Family);

    /// <summary>A style sheet entry, read before the styles are made so that every link can be resolved.</summary>
    private sealed class StyleEntry
    {
        public StyleType Type { get; set; } = StyleType.Paragraph;

        public int Index { get; set; }

        public string Name { get; set; } = "";

        public RunFormat Char { get; set; } = new();

        public ParagraphFormat Para { get; set; } = new();

        public TableFormat Table { get; } = new();

        public int BasedOn { get; set; } = -1;

        public int Next { get; set; } = -1;

        public int Link { get; set; } = -1;

        public bool Hidden { get; set; }

        public bool Primary { get; set; }

        public int? Priority { get; set; }

        public int ListOverride { get; set; }

        public int ListLevel { get; set; }
    }

    /// <summary>The text encoding of font <paramref name="index"/>'s character set; the document's code page for -1.</summary>
    private Encoding EncodingOf(int index)
    {
        if (index < 0)
        {
            index = defaultFont;
        }

        int codePage = ansiCodePage;
        if (fonts.TryGetValue(index, out FontEntry? font))
        {
            codePage = font.CodePage > 0 ? font.CodePage : CodePageOf(font.Charset) ?? ansiCodePage;
        }

        if (!encodings.TryGetValue(codePage, out Encoding? encoding))
        {
            encoding = EncodingFor(codePage);
            encodings[codePage] = encoding;
        }

        return encoding;
    }

    private static Encoding EncodingFor(int codePage)
    {
        if (codePage == 28591)
        {
            return Encoding.Latin1;
        }

        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.GetEncoding(codePage);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1;
        }
    }

    /// <summary>The Windows code page of an RTF character set (<c>\fcharset</c>), or null for the document's own.</summary>
    private static int? CodePageOf(int charset) => charset switch
    {
        0 => 1252,
        2 => 28591, // Symbol fonts: each byte is its own character, which the symbol font maps.
        77 => 10000,
        78 => 10001,
        79 => 10003,
        80 => 10008,
        81 => 10002,
        83 => 10005,
        84 => 10004,
        85 => 10006,
        86 => 10081,
        87 => 10021,
        88 => 10029,
        89 => 10007,
        128 => 932,
        129 => 949,
        130 => 1361,
        134 => 936,
        136 => 950,
        161 => 1253,
        162 => 1254,
        163 => 1258,
        177 => 1255,
        178 => 1256,
        186 => 1257,
        204 => 1251,
        222 => 874,
        238 => 1250,
        254 => 437,
        255 => 850,
        _ => null,
    };

    private string? FontName(int index) => fonts.TryGetValue(index, out FontEntry? font) && font.Name.Length > 0 ? font.Name : null;

    private DocumentColor Color(int index) => index >= 0 && index < colors.Count ? colors[index] : DocumentColor.Auto;

    private string Author(int index) => index >= 0 && index < authors.Count ? authors[index] : "";

    private ParagraphStyle? ParagraphStyleAt(int index) => styles.GetValueOrDefault(index) as ParagraphStyle;

    private CharacterStyle? CharacterStyleAt(int index) => styles.GetValueOrDefault(index) as CharacterStyle;

    // ---- Font table ----

    private void ReadFontTable()
    {
        int groupDepth = depth;
        TextCollector name = new(this);
        int number = -1;
        int charset = 1;
        int codePage = 0;
        string? alt = null;
        string? family = null;

        while (true)
        {
            RtfTokenKind kind = tokens.Next();
            if (kind == RtfTokenKind.End)
            {
                Finish();
                return;
            }

            if (kind == RtfTokenKind.GroupStart)
            {
                Push(tokens.Position - 1);
                if (depth > groupDepth + 1)
                {
                    // A group inside an entry: the alternative name is kept, panose numbers and the rest skipped.
                    RtfTokenKind first = tokens.Next();
                    if (first == RtfTokenKind.ControlSymbol && tokens.Symbol == '*')
                    {
                        first = tokens.Next();
                    }

                    if (first == RtfTokenKind.ControlWord && tokens.Word == "falt")
                    {
                        alt = ReadText();
                    }
                    else
                    {
                        SkipRest(first);
                    }
                }

                continue;
            }

            if (kind == RtfTokenKind.GroupEnd)
            {
                if (depth == groupDepth + 1 || depth == groupDepth)
                {
                    Finish();
                }

                Pop();
                if (depth < groupDepth)
                {
                    return;
                }

                continue;
            }

            if (kind == RtfTokenKind.ControlWord)
            {
                switch (tokens.Word)
                {
                    case "f" when tokens.HasParameter:
                        Finish();
                        number = tokens.Parameter;
                        continue;
                    case "fcharset" when tokens.HasParameter:
                        charset = tokens.Parameter;
                        continue;
                    case "cpg" when tokens.HasParameter:
                        codePage = tokens.Parameter;
                        continue;
                    case "froman" or "fswiss" or "fmodern" or "fscript" or "fdecor" or "ftech" or "fbidi" or "fnil":
                        family = tokens.Word switch
                        {
                            "froman" => "roman",
                            "fswiss" => "swiss",
                            "fmodern" => "modern",
                            "fscript" => "script",
                            "fdecor" => "decorative",
                            _ => "auto",
                        };
                        continue;
                }
            }

            if (number >= 0)
            {
                name.Encoding = EncodingFor(codePage > 0 ? codePage : CodePageOf(charset) ?? ansiCodePage);
                // The name ends at a semicolon; looking for it in the token, not the name so far, keeps a long name linear.
                if (name.Add(kind) && kind == RtfTokenKind.Text && tokens.Text.Contains((byte)';'))
                {
                    Finish();
                }
            }
        }

        void Finish()
        {
            if (number >= 0)
            {
                string value = name.Text(toSemicolon: true);
                if (fonts.Count < MaxEntries || fonts.ContainsKey(number))
                {
                    fonts[number] = new FontEntry(value, charset, codePage, alt, family);
                }
                if (value.Length > 0)
                {
                    document.FontHints[value] = (alt, family);
                }
            }

            number = -1;
            charset = 1;
            codePage = 0;
            alt = null;
            family = null;
            name.Clear();
        }
    }

    // ---- Colour table ----

    private void ReadColorTable()
    {
        int groupDepth = depth;
        int red = -1;
        int green = -1;
        int blue = -1;
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    return;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    SkipDestination();
                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < groupDepth)
                    {
                        return;
                    }

                    break;
                case RtfTokenKind.ControlWord when tokens.HasParameter:
                    switch (tokens.Word)
                    {
                        case "red":
                            red = Math.Clamp(tokens.Parameter, 0, 255);
                            break;
                        case "green":
                            green = Math.Clamp(tokens.Parameter, 0, 255);
                            break;
                        case "blue":
                            blue = Math.Clamp(tokens.Parameter, 0, 255);
                            break;
                    }

                    break;
                case RtfTokenKind.Text:
                    foreach (byte b in tokens.Text)
                    {
                        if (b != (byte)';' || colors.Count >= MaxEntries)
                        {
                            continue;
                        }

                        // An entry with no components is automatic, as the first entry usually is.
                        colors.Add(red < 0 && green < 0 && blue < 0
                            ? DocumentColor.Auto
                            : DocumentColor.FromRgb(new Rgba8((byte)Math.Max(red, 0), (byte)Math.Max(green, 0), (byte)Math.Max(blue, 0), 255)));
                        red = green = blue = -1;
                    }

                    break;
            }
        }
    }

    // ---- Style sheet ----

    private void ReadStyleSheet()
    {
        int groupDepth = depth;
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    MakeStyles();
                    return;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    ReadStyleEntry();
                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < groupDepth)
                    {
                        MakeStyles();
                        return;
                    }

                    break;
            }
        }
    }

    private void ReadStyleEntry()
    {
        int entryDepth = depth;
        state.ResetCharacter();
        state.ResetParagraph();
        StyleEntry entry = new();
        TextCollector name = new(this);
        bool section = false;
        while (true)
        {
            RtfTokenKind kind = tokens.Next();
            switch (kind)
            {
                case RtfTokenKind.End:
                    return;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    SkipDestination();
                    continue;
                case RtfTokenKind.GroupEnd:
                    entry.Name = name.Text(toSemicolon: true);
                    entry.Char = state.Char;
                    entry.Para = state.Para;
                    entry.ListOverride = state.ListOverride;
                    entry.ListLevel = state.ListLevel;
                    if (!section && entry.Name.Length > 0 && styleEntries.Count < MaxEntries)
                    {
                        styleEntries.Add(entry);
                    }

                    Pop();
                    if (depth < entryDepth)
                    {
                        return;
                    }

                    continue;
                case RtfTokenKind.ControlSymbol when tokens.Symbol == '*':
                    continue;
                case RtfTokenKind.ControlWord:
                    string word = tokens.Word;
                    bool has = tokens.HasParameter;
                    int p = tokens.Parameter;
                    switch (word)
                    {
                        case "s" when has:
                            entry.Type = StyleType.Paragraph;
                            entry.Index = p;
                            continue;
                        case "cs" when has:
                            entry.Type = StyleType.Character;
                            entry.Index = p;
                            continue;
                        case "ts" when has:
                            entry.Type = StyleType.Table;
                            entry.Index = p;
                            continue;
                        case "ds" when has:
                            section = true;
                            continue;
                        case "sbasedon" when has:
                            entry.BasedOn = p;
                            continue;
                        case "snext" when has:
                            entry.Next = p;
                            continue;
                        case "slink" when has:
                            entry.Link = p;
                            continue;
                        case "shidden" or "ssemihidden":
                            entry.Hidden = !has || p != 0;
                            continue;
                        case "sqformat":
                            entry.Primary = true;
                            continue;
                        case "spriority" when has:
                            entry.Priority = p;
                            continue;
                        case "trpaddl" or "trpaddr" or "trpaddt" or "trpaddb" when has:
                            SetCellMargin(entry.Table, word[^1], p);
                            continue;
                        case "tblind" when has:
                            entry.Table.Indent = Twips(p);
                            continue;
                    }

                    if (word.StartsWith("ts", StringComparison.Ordinal) || word.StartsWith("tr", StringComparison.Ordinal)
                        || word.StartsWith("cl", StringComparison.Ordinal) || word.StartsWith("tbl", StringComparison.Ordinal))
                    {
                        // Table style regions and their borders are not read: the borders after them must not land elsewhere.
                        state.Border = BorderTarget.None;
                        continue;
                    }

                    if (name.Add(kind))
                    {
                        continue;
                    }

                    if (word is "pard" or "plain")
                    {
                        continue;
                    }

                    _ = CharacterWord(word, has, p) || ParagraphWord(word, has, p);
                    continue;
                default:
                    name.Add(kind);
                    continue;
            }
        }
    }

    private static void SetCellMargin(TableFormat format, char side, int twips)
    {
        double value = Math.Max(0, Twips(twips));
        switch (side)
        {
            case 'l':
                format.CellMarginLeft = value;
                break;
            case 'r':
                format.CellMarginRight = value;
                break;
            case 't':
                format.CellMarginTop = value;
                break;
            default:
                format.CellMarginBottom = value;
                break;
        }
    }

    /// <summary>Makes the styles the style sheet named, with their links.</summary>
    private void MakeStyles()
    {
        HashSet<string> ids = new(document.Styles.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        foreach (StyleEntry entry in styleEntries)
        {
            if (styles.ContainsKey(entry.Index))
            {
                continue;
            }

            madeFrom[entry.Index] = entry;

            string id = StyleId(entry.Name, ids);
            Style style = entry.Type switch
            {
                StyleType.Character => document.Styles.AddCharacterStyle(id, entry.Name),
                StyleType.Table => document.Styles.AddTableStyle(id, entry.Name),
                _ => document.Styles.AddParagraphStyle(id, entry.Name),
            };
            styles[entry.Index] = style;
            style.Hidden = entry.Hidden;
            style.IsPrimary = entry.Primary;
            style.Priority = entry.Priority;
            switch (style)
            {
                case ParagraphStyle paragraph:
                    paragraph.ParagraphFormat.CopyRaw(entry.Para);
                    paragraph.RunFormat.CopyRaw(entry.Char);
                    if (entry.Index == 0)
                    {
                        document.Styles.SetDefault(paragraph);
                    }

                    break;
                case CharacterStyle character:
                    character.RunFormat.CopyRaw(entry.Char);
                    if (entry.Name.Equals("Default Paragraph Font", StringComparison.OrdinalIgnoreCase))
                    {
                        document.Styles.SetDefault(character);
                    }

                    break;
                case TableStyle table:
                    table.RunFormat.CopyRaw(entry.Char);
                    table.ParagraphFormat.CopyRaw(entry.Para);
                    table.TableFormat.CopyRaw(entry.Table);
                    if (entry.Name.Equals("Normal Table", StringComparison.OrdinalIgnoreCase))
                    {
                        document.Styles.SetDefault(table);
                    }

                    break;
            }
        }

        foreach (StyleEntry entry in styleEntries)
        {
            if (!styles.TryGetValue(entry.Index, out Style? style))
            {
                continue;
            }

            if (entry.BasedOn >= 0 && entry.BasedOn != entry.Index && styles.TryGetValue(entry.BasedOn, out Style? parent) && parent.Type == style.Type)
            {
                try
                {
                    style.BasedOn = parent;
                }
                catch (ArgumentException)
                {
                    // A loop of based-on links; the style keeps no base.
                }
            }

            if (style is ParagraphStyle paragraph)
            {
                if (ParagraphStyleAt(entry.Next) is { } next)
                {
                    paragraph.Next = next;
                }

                if (CharacterStyleAt(entry.Link) is { } linked)
                {
                    paragraph.Linked = linked;
                }
            }
            else if (style is CharacterStyle character && ParagraphStyleAt(entry.Link) is { } linkedParagraph)
            {
                character.Linked = linkedParagraph;
            }
        }
    }

    /// <summary>
    /// A style repeats what it inherits, as Word writes it; the style keeps only what differs from its base, or, with no
    /// base, a paragraph style what differs from the document defaults (their fonts known once the header ends). Table
    /// styles keep what they say.
    /// </summary>
    private void SubtractInherited()
    {
        foreach ((int index, StyleEntry entry) in madeFrom)
        {
            Style style = styles[index];
            if (style is TableStyle)
            {
                continue;
            }

            (ParagraphFormat Para, RunFormat Char)? inherited = BaseEntry(style, entry) is { } parent ? (parent.Para, parent.Char)
                : style is ParagraphStyle ? (document.Styles.DefaultParagraphFormat, document.Styles.DefaultRunFormat) : null;
            if (inherited is not { } from)
            {
                continue;
            }

            if (style is ParagraphStyle paragraph)
            {
                RtfStyleFormats.Subtract(paragraph.ParagraphFormat, from.Para);
                RtfStyleFormats.Subtract(paragraph.RunFormat, from.Char);
            }
            else if (style is CharacterStyle character)
            {
                RtfStyleFormats.Subtract(character.RunFormat, from.Char);
            }
        }

        // A writer that writes only what a style changes (LibreOffice) may still repeat a value the style inherits from
        // further up its chain; dropping it, bases first, leaves the formatting in effect as it was and reads such a style
        // as one written in full does.
        foreach (Style style in madeFrom.Keys.Select(i => styles[i]).Where(s => s is not TableStyle).OrderBy(RtfStyleFormats.Depth))
        {
            RtfStyleFormats.DropRedundant(style);
        }
    }

    /// <summary>The style sheet entry <paramref name="style"/>'s base was made from, or null when it has no base.</summary>
    private StyleEntry? BaseEntry(Style style, StyleEntry entry)
    {
        return style.BasedOn is { } parent && madeFrom.TryGetValue(entry.BasedOn, out StyleEntry? parentEntry) && styles[entry.BasedOn] == parent
            ? parentEntry
            : null;
    }

    /// <summary>A style id from a style's name: Word's own id for a built-in style, else the name without spaces, unique.</summary>
    private string StyleId(string name, HashSet<string> used)
    {
        string id = "";
        foreach (BuiltInStyle builtIn in Enum.GetValues<BuiltInStyle>())
        {
            if (BuiltInStyles.Name(builtIn).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                id = BuiltInStyles.Id(builtIn);
                break;
            }
        }

        if (id.Length == 0)
        {
            StringBuilder text = new();
            bool upper = true;
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c))
                {
                    text.Append(upper ? char.ToUpperInvariant(c) : c);
                    upper = false;
                }
                else
                {
                    upper = true;
                }
            }

            id = text.Length > 0 ? text.ToString() : "Style";
        }

        // Many styles with one name try each suffix once: the next one to try is kept per id.
        string unique = id;
        int n = styleIdSuffixes.GetValueOrDefault(id, 1);
        while (!used.Add(unique))
        {
            unique = id + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            n++;
        }

        styleIdSuffixes[id] = n;
        return unique;
    }

    // ---- Lists ----

    private sealed class LevelEntry
    {
        public NumberStyle Style { get; set; }

        public ParagraphAlignment Alignment { get; set; }

        public ListLabelSuffix Suffix { get; set; }

        public int Start { get; set; } = 1;

        public string Text { get; set; } = "";

        public bool Legal { get; set; }

        public bool NoRestart { get; set; }

        public RunFormat Char { get; set; } = new();

        public ParagraphFormat Para { get; set; } = new();
    }

    private void ReadListTable()
    {
        int groupDepth = depth;
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    return;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    RtfTokenKind head = tokens.Next();
                    if (head == RtfTokenKind.ControlWord && tokens.Word == "list")
                    {
                        ReadList();
                    }
                    else
                    {
                        SkipRest(head);
                    }

                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < groupDepth)
                    {
                        return;
                    }

                    break;
            }
        }
    }

    private void ReadList()
    {
        int listDepth = depth;
        List<LevelEntry> levels = [];
        int id = 0;
        ListKind kind = ListKind.Multilevel;
        string? name = null;
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    return;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    RtfTokenKind first = tokens.Next();
                    if (first == RtfTokenKind.ControlWord && tokens.Word == "listlevel" && levels.Count < 9)
                    {
                        levels.Add(ReadListLevel());
                    }
                    else if (first == RtfTokenKind.ControlWord && tokens.Word == "listname")
                    {
                        name = ReadText(toSemicolon: true);
                    }
                    else
                    {
                        SkipRest(first);
                    }

                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < listDepth)
                    {
                        if (!listDefinitions.ContainsKey(id) && listDefinitions.Count < MaxEntries)
                        {
                            ListDefinition definition = document.Lists.AddDefinition(listDefinitions.Count + 1);
                            definition.Kind = kind;
                            definition.Name = string.IsNullOrEmpty(name) ? null : name;
                            for (int i = 0; i < levels.Count; i++)
                            {
                                FillLevel(definition.Levels[i], levels[i]);
                            }

                            listDefinitions[id] = definition;
                        }

                        return;
                    }

                    break;
                case RtfTokenKind.ControlWord:
                    switch (tokens.Word)
                    {
                        case "listid" when tokens.HasParameter:
                            id = tokens.Parameter;
                            break;
                        case "listsimple":
                            kind = tokens.HasParameter && tokens.Parameter == 0 ? kind : ListKind.SingleLevel;
                            break;
                        case "listhybrid":
                            kind = ListKind.HybridMultilevel;
                            break;
                    }

                    break;
            }
        }
    }

    private LevelEntry ReadListLevel()
    {
        int levelDepth = depth;
        state.ResetCharacter();
        state.ResetParagraph();
        LevelEntry level = new();
        string levelText = "";
        HashSet<int> numbers = [];
        bool newStyle = false;
        while (true)
        {
            RtfTokenKind kind = tokens.Next();
            switch (kind)
            {
                case RtfTokenKind.End:
                    return level;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    RtfTokenKind first = tokens.Next();
                    if (first == RtfTokenKind.ControlWord && tokens.Word == "leveltext")
                    {
                        levelText = ReadText(trim: false);
                    }
                    else if (first == RtfTokenKind.ControlWord && tokens.Word == "levelnumbers")
                    {
                        numbers.UnionWith(ReadText(trim: false).Where(c => c is > '\0' and < ';').Select(c => (int)c));
                    }
                    else
                    {
                        SkipRest(first);
                    }

                    break;
                case RtfTokenKind.GroupEnd:
                    level.Char = state.Char;
                    level.Para = state.Para;
                    level.Text = LevelText(levelText, numbers);
                    Pop();
                    if (depth < levelDepth)
                    {
                        return level;
                    }

                    break;
                case RtfTokenKind.ControlWord:
                    string word = tokens.Word;
                    int p = tokens.Parameter;
                    bool has = tokens.HasParameter;
                    switch (word)
                    {
                        case "levelnfc" when has && !newStyle:
                            level.Style = NumberStyleOf(p);
                            break;
                        case "levelnfcn" when has:
                            level.Style = NumberStyleOf(p);
                            newStyle = true;
                            break;
                        case "leveljc" or "leveljcn" when has:
                            level.Alignment = p switch { 1 => ParagraphAlignment.Center, 2 => ParagraphAlignment.Right, _ => ParagraphAlignment.Left };
                            break;
                        case "levelfollow" when has:
                            level.Suffix = p switch { 1 => ListLabelSuffix.Space, 2 => ListLabelSuffix.Nothing, _ => ListLabelSuffix.Tab };
                            break;
                        case "levelstartat" when has:
                            level.Start = Math.Max(0, p);
                            break;
                        case "levellegal":
                            level.Legal = !has || p != 0;
                            break;
                        case "levelnorestart":
                            level.NoRestart = !has || p != 0;
                            break;
                        default:
                            if (!word.StartsWith("level", StringComparison.Ordinal) && word is not ("pard" or "plain"))
                            {
                                _ = CharacterWord(word, has, p) || ParagraphWord(word, has, p);
                            }

                            break;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Turns <c>\leveltext</c> (a length, then the text with level numbers as bytes 0 to 8 at the places
    /// <c>\levelnumbers</c> names) into Word's "%1." form.
    /// </summary>
    private static string LevelText(string text, HashSet<int> numbers)
    {
        if (text.Length == 0)
        {
            return "";
        }

        int length = Math.Min(text[0], text.Length - 1);
        StringBuilder result = new();
        for (int i = 1; i <= length; i++)
        {
            char c = text[i];
            if (numbers.Contains(i) && c <= '\u0008')
            {
                result.Append('%').Append((char)('1' + c));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private static NumberStyle NumberStyleOf(int code) => code switch
    {
        1 => NumberStyle.UpperRoman,
        2 => NumberStyle.LowerRoman,
        3 => NumberStyle.UpperLetter,
        4 => NumberStyle.LowerLetter,
        5 => NumberStyle.Ordinal,
        6 => NumberStyle.CardinalText,
        7 => NumberStyle.OrdinalText,
        22 => NumberStyle.DecimalZero,
        23 => NumberStyle.Bullet,
        255 => NumberStyle.None,
        _ => NumberStyle.Decimal,
    };

    private static void FillLevel(ListLevel target, LevelEntry level)
    {
        target.NumberStyle = level.Style;
        target.Alignment = level.Alignment;
        target.Suffix = level.Suffix;
        target.StartAt = level.Start;
        target.LevelText = level.Text;
        target.IsLegal = level.Legal;
        if (level.NoRestart)
        {
            target.RestartAfterLevel = 0;
        }

        target.ParagraphFormat.CopyRaw(level.Para);
        target.RunFormat.CopyRaw(level.Char);
    }

    private void ReadListOverrideTable()
    {
        int groupDepth = depth;
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    return;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    RtfTokenKind head = tokens.Next();
                    if (head == RtfTokenKind.ControlWord && tokens.Word == "listoverride")
                    {
                        ReadListOverride();
                    }
                    else
                    {
                        SkipRest(head);
                    }

                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < groupDepth)
                    {
                        return;
                    }

                    break;
            }
        }
    }

    private void ReadListOverride()
    {
        int overrideDepth = depth;
        int listId = 0;
        int ls = 0;
        List<(int? Start, LevelEntry? Level)> levels = [];
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    return;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    RtfTokenKind head = tokens.Next();
                    if (head == RtfTokenKind.ControlWord && tokens.Word == "lfolevel" && levels.Count < 9)
                    {
                        levels.Add(ReadOverrideLevel());
                    }
                    else
                    {
                        SkipRest(head);
                    }

                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < overrideDepth)
                    {
                        if (ls > 0 && lists.Count < MaxEntries && !lists.ContainsKey(ls) && listDefinitions.TryGetValue(listId, out ListDefinition? definition))
                        {
                            List list = document.Lists.Add(ls, definition);
                            for (int i = 0; i < levels.Count; i++)
                            {
                                if (levels[i].Start is int start)
                                {
                                    list.Overrides[i].StartAt = Math.Max(0, start);
                                }

                                if (levels[i].Level is { } level)
                                {
                                    FillLevel(list.Overrides[i].CreateLevel(), level);
                                }
                            }

                            lists[ls] = list;
                        }

                        return;
                    }

                    break;
                case RtfTokenKind.ControlWord:
                    if (tokens.Word == "listid" && tokens.HasParameter)
                    {
                        listId = tokens.Parameter;
                    }
                    else if (tokens.Word == "ls" && tokens.HasParameter)
                    {
                        ls = tokens.Parameter;
                    }

                    break;
            }
        }
    }

    private (int? Start, LevelEntry? Level) ReadOverrideLevel()
    {
        int levelDepth = depth;
        bool startOverride = false;
        int? start = null;
        LevelEntry? level = null;
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    return (start, level);
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    RtfTokenKind head = tokens.Next();
                    if (head == RtfTokenKind.ControlWord && tokens.Word == "listlevel")
                    {
                        level = ReadListLevel();
                    }
                    else
                    {
                        SkipRest(head);
                    }

                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < levelDepth)
                    {
                        return (startOverride ? start ?? level?.Start ?? 1 : null, level);
                    }

                    break;
                case RtfTokenKind.ControlWord:
                    if (tokens.Word == "listoverridestartat")
                    {
                        startOverride = true;
                    }
                    else if (tokens.Word == "levelstartat" && tokens.HasParameter)
                    {
                        start = tokens.Parameter;
                    }

                    break;
            }
        }
    }

    /// <summary>The list an old-style <c>\pn</c> paragraph number makes: one single-level list per distinct number format.</summary>
    private List? ListOf(PnState pn)
    {
        if (!pnLists.TryGetValue(pn, out List? list))
        {
            if (pnLists.Count >= MaxEntries)
            {
                return null;
            }

            ListDefinition definition = document.Lists.AddDefinition(listDefinitions.Count + pnLists.Count + 1);
            definition.Kind = ListKind.SingleLevel;
            ListLevel level = definition.Levels[0];
            level.NumberStyle = pn.Bullet ? NumberStyle.Bullet : pn.Style;
            level.LevelText = pn.Bullet ? pn.Before + pn.After : pn.Before + "%1" + pn.After;
            level.StartAt = pn.Start;
            if (FontName(pn.Font) is { } font)
            {
                level.RunFormat.FontAscii = font;
                level.RunFormat.FontHighAnsi = font;
            }

            // The next id no \ls list holds; ids only grow, so finding them all costs one pass.
            while (lists.ContainsKey(nextPnListId))
            {
                nextPnListId++;
            }

            list = document.Lists.Add(nextPnListId++, definition);
            pnLists[pn] = list;
        }

        return list;
    }

    // ---- Revision authors, information and defaults ----

    private void ReadRevisionTable()
    {
        int groupDepth = depth;
        TextCollector name = new(this);
        while (true)
        {
            RtfTokenKind kind = tokens.Next();
            switch (kind)
            {
                case RtfTokenKind.End:
                    return;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    if (depth == groupDepth + 1)
                    {
                        // An author's own group starts a new name; a group inside it, such as the {} after a \uN, does not.
                        name.Clear();
                    }

                    break;
                case RtfTokenKind.GroupEnd:
                    if (depth == groupDepth + 1 && authors.Count < MaxEntries)
                    {
                        authors.Add(name.Text(toSemicolon: true));
                    }

                    Pop();
                    if (depth < groupDepth)
                    {
                        return;
                    }

                    break;
                default:
                    name.Add(kind);
                    break;
            }
        }
    }

    private void ReadInfo()
    {
        int groupDepth = depth;
        DocumentProperties properties = document.Properties;
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    return;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    RtfTokenKind first = tokens.Next();
                    bool star = first == RtfTokenKind.ControlSymbol && tokens.Symbol == '*';
                    if (star)
                    {
                        first = tokens.Next();
                    }

                    if (first != RtfTokenKind.ControlWord)
                    {
                        SkipRest(first);

                        break;
                    }

                    switch (tokens.Word)
                    {
                        case "title":
                            properties.Title = NullIfEmpty(ReadText());
                            break;
                        case "subject":
                            properties.Subject = NullIfEmpty(ReadText());
                            break;
                        case "author":
                            properties.Author = NullIfEmpty(ReadText());
                            break;
                        case "keywords":
                            properties.Keywords = NullIfEmpty(ReadText());
                            break;
                        case "doccomm":
                            properties.Comments = NullIfEmpty(ReadText());
                            break;
                        case "category":
                            properties.Category = NullIfEmpty(ReadText());
                            break;
                        case "operator":
                            properties.LastSavedBy = NullIfEmpty(ReadText());
                            break;
                        case "creatim":
                            properties.Created = ReadInfoDate();
                            break;
                        case "revtim":
                            properties.Saved = ReadInfoDate();
                            break;
                        case "printim":
                            properties.Printed = ReadInfoDate();
                            break;
                        case "passwordhash":
                            ReadPasswordHash();
                            break;
                        default:
                            SkipDestination(keep: star);
                            break;
                    }

                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < groupDepth)
                    {
                        return;
                    }

                    break;
            }
        }

        static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
    }

    private DateTimeOffset? ReadInfoDate()
    {
        int groupDepth = depth;
        int year = 0;
        int month = 1;
        int day = 1;
        int hour = 0;
        int minute = 0;
        int second = 0;
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    return null;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    SkipDestination();
                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < groupDepth)
                    {
                        return year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
                            && hour is >= 0 and <= 23 && minute is >= 0 and <= 59 && second is >= 0 and <= 59
                            ? new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero)
                            : null;
                    }

                    break;
                case RtfTokenKind.ControlWord when tokens.HasParameter:
                    switch (tokens.Word)
                    {
                        case "yr":
                            year = tokens.Parameter;
                            break;
                        case "mo":
                            month = tokens.Parameter;
                            break;
                        case "dy":
                            day = tokens.Parameter;
                            break;
                        case "hr":
                            hour = tokens.Parameter;
                            break;
                        case "min":
                            minute = tokens.Parameter;
                            break;
                        case "sec":
                            second = tokens.Parameter;
                            break;
                    }

                    break;
            }
        }
    }

    private void ReadUserProperties()
    {
        int groupDepth = depth;
        string? name = null;
        while (true)
        {
            switch (tokens.Next())
            {
                case RtfTokenKind.End:
                    return;
                case RtfTokenKind.GroupStart:
                    Push(tokens.Position - 1);
                    RtfTokenKind head = tokens.Next();
                    if (head == RtfTokenKind.ControlWord && tokens.Word is "propname" or "staticval")
                    {
                        bool isName = tokens.Word == "propname";
                        string value = ReadText();
                        if (isName)
                        {
                            name = value;
                        }
                        else if (!string.IsNullOrEmpty(name))
                        {
                            document.Properties.SetCustom(name, value);
                            name = null;
                        }
                    }
                    else
                    {
                        SkipRest(head);
                    }

                    break;
                case RtfTokenKind.GroupEnd:
                    Pop();
                    if (depth < groupDepth)
                    {
                        return;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Ends the header once the body starts: the default fonts, and the list each style names (the list tables come after the
    /// style sheet).
    /// </summary>
    private void FinishHeader()
    {
        if (bodyStarted)
        {
            return;
        }

        bodyStarted = true;
        RunFormat defaults = document.Styles.DefaultRunFormat;
        defaults.FontAscii ??= FontName(defaultFonts[0]) ?? FontName(defaultFont);
        defaults.FontHighAnsi ??= FontName(defaultFonts[1]) ?? FontName(defaultFont);
        defaults.FontEastAsia ??= FontName(defaultFonts[2]) ?? FontName(defaultFont);
        defaults.FontComplexScript ??= FontName(defaultFonts[3]) ?? FontName(defaultFont);
        SubtractInherited();
        if (document.Styles.DefaultParagraphStyle is null)
        {
            ParagraphStyle normal = document.Styles[BuiltInStyles.Id(BuiltInStyle.Normal)] as ParagraphStyle
                ?? document.Styles.AddParagraphStyle(
                    StyleId(BuiltInStyles.Name(BuiltInStyle.Normal), new(document.Styles.Select(s => s.Id), StringComparer.OrdinalIgnoreCase)),
                    BuiltInStyles.Name(BuiltInStyle.Normal));
            document.Styles.SetDefault(normal);
            styles.TryAdd(0, normal);
        }

        foreach ((int index, StyleEntry entry) in madeFrom)
        {
            // A list the style inherits is its base's, not its own.
            if (entry.ListOverride <= 0 || styles[index] is not ParagraphStyle style || !lists.TryGetValue(entry.ListOverride, out List? list)
                || (BaseEntry(style, entry) is { } parent && (parent.ListOverride, parent.ListLevel) == (entry.ListOverride, entry.ListLevel)))
            {
                continue;
            }

            style.ParagraphFormat.List = list;
            style.ParagraphFormat.ListLevel = entry.ListLevel;
        }
    }
}
