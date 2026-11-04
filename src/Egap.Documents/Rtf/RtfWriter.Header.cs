namespace Egap.Documents.Rtf;

/// <summary>The header: the font and colour tables, defaults, style sheet, lists, revision authors, information and settings.</summary>
internal sealed partial class RtfWriter
{
    private readonly Dictionary<Style, int> styleIndexes = [];
    private readonly Dictionary<string, int> authors = new(StringComparer.Ordinal) { ["Unknown"] = 0 };
    private readonly List<string> authorList = ["Unknown"];

    /// <summary>
    /// A style's number in the style sheet: the default paragraph style is 0 (<c>\s0</c>, Normal), the others count up in the
    /// document's order across paragraph, character and table styles. List styles have none in RTF.
    /// </summary>
    private int StyleIndex(Style? style)
    {
        if (styleIndexes.Count == 0)
        {
            ParagraphStyle? normal = document.Styles.DefaultParagraphStyle;
            if (normal is not null)
            {
                styleIndexes[normal] = 0;
            }

            int next = 1;
            foreach (Style s in document.Styles)
            {
                if (s is not ListStyle && s != normal)
                {
                    styleIndexes[s] = next++;
                }
            }
        }

        return style is not null && styleIndexes.TryGetValue(style, out int index) ? index : 0;
    }

    /// <summary>A revision author's index in the revision table, added when new; the table starts with "Unknown".</summary>
    private int Author(string name)
    {
        if (!authors.TryGetValue(name, out int index))
        {
            index = authorList.Count;
            authorList.Add(name);
            authors[name] = index;
        }

        return index;
    }

    /// <summary>The document's default font (<c>\deff</c>), written only when the defaults name all four font slots.</summary>
    private void WriteDefaultFont()
    {
        RunFormat defaults = document.Styles.DefaultRunFormat;
        string? ascii = FontOf(defaults.FontAscii, defaults.ThemeFontAscii);
        string? high = FontOf(defaults.FontHighAnsi, defaults.ThemeFontHighAnsi);
        string? eastAsia = FontOf(defaults.FontEastAsia, defaults.ThemeFontEastAsia);
        string? complex = FontOf(defaults.FontComplexScript, defaults.ThemeFontComplexScript);
        if (ascii is not null && high is not null && eastAsia is not null && complex is not null)
        {
            // A slot the defaults leave empty would take the default font on reading, so it is only named when all are set.
            Word("deff", Font(ascii));
            Word("stshfdbch", Font(eastAsia));
            Word("stshfloch", Font(ascii));
            Word("stshfhich", Font(high));
            Word("stshfbi", Font(complex));
        }

        LanguageWord("deflang", defaults.Language);
        LanguageWord("deflangfe", defaults.LanguageEastAsia);
    }

    private void WriteFontTable()
    {
        Open();
        Word("fonttbl");
        foreach ((string name, int index) in fonts.OrderBy(f => f.Value))
        {
            (string? alt, string? kind) = document.FontHints.TryGetValue(name, out (string? AltName, string? Kind) hint) ? hint : (null, null);
            Open();
            Word("f", index);
            Word(kind switch
            {
                "roman" => "froman",
                "swiss" => "fswiss",
                "modern" => "fmodern",
                "script" => "fscript",
                "decorative" => "fdecor",
                _ => "fnil",
            });
            Word("fcharset", IsSymbolFont(name) ? 2 : 0);
            Name(name);
            if (!string.IsNullOrEmpty(alt))
            {
                Destination("falt");
                Name(alt);
                Close();
            }

            output.Append(';');
            Close();
            output.AppendLine();
        }

        Close();
    }

    /// <summary>Fonts whose characters are symbols: Word lists them with the symbol character set.</summary>
    private static bool IsSymbolFont(string name)
    {
        return name.Equals("Symbol", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Wingdings", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Webdings", StringComparison.OrdinalIgnoreCase) || name.Equals("Marlett", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A name in a table, where a semicolon ends the entry: semicolons in the name are left out.</summary>
    private void Name(string name)
    {
        Text(name.Contains(';', StringComparison.Ordinal) ? name.Replace(";", "", StringComparison.Ordinal) : name);
    }

    private void WriteColorTable()
    {
        Open();
        Word("colortbl");
        output.Append(';');
        pendingSpace = false;
        foreach (Egami.Pixels.Rgba8 color in colorList)
        {
            Word("red", color.R);
            Word("green", color.G);
            Word("blue", color.B);
            output.Append(';');
            pendingSpace = false;
        }

        Close();
        output.AppendLine();
    }

    /// <summary>The document's default character and paragraph formatting.</summary>
    private void WriteDefaults()
    {
        Destination("defchp");
        RunWords(document.Styles.DefaultRunFormat, null);
        if (document.Styles.DefaultRunFormat.Size is null)
        {
            // RTF's text is 12 points when nothing says; the model's is 10.
            Word("fs", 20);
        }

        Close();
        Destination("defpap");
        ParagraphWords(document.Styles.DefaultParagraphFormat);
        Close();
        output.AppendLine();
    }

    // ---- Style sheet ----

    private void WriteStyleSheet()
    {
        Open();
        Word("stylesheet");
        ParagraphStyle? normal = document.Styles.DefaultParagraphStyle;
        IEnumerable<Style> ordered = normal is null ? document.Styles : [normal, .. document.Styles.Where(s => s != normal)];
        foreach (Style style in ordered)
        {
            if (style is ListStyle)
            {
                continue;
            }

            int index = StyleIndex(style);
            (ParagraphFormat inheritedParagraph, RunFormat inheritedRun) = RtfStyleFormats.Inherited(style);
            switch (style)
            {
                case ParagraphStyle paragraph:
                    Open();
                    Word("s", index);
                    ParagraphWords(Effective(inheritedParagraph, paragraph.ParagraphFormat));
                    RunWords(Effective(inheritedRun, paragraph.RunFormat), null);
                    break;
                case CharacterStyle character:
                    Destination("cs");
                    output.Append(index);
                    Word("additive");
                    RunWords(Effective(inheritedRun, character.RunFormat), null);
                    break;
                case TableStyle table:
                    Destination("ts");
                    output.Append(index);
                    Word("tsrowd");
                    TableStyleWords(table.TableFormat);
                    ParagraphWords(table.ParagraphFormat);
                    RunWords(table.RunFormat, null);
                    break;
                default:
                    continue;
            }

            StyleLinks(style);
            Space();
            Name(style.Name);
            output.Append(';');
            Close();
            output.AppendLine();
        }

        Close();
        output.AppendLine();
    }

    private void StyleLinks(Style style)
    {
        if (style.BasedOn is { IsInDocument: true } basedOn && basedOn.GetType() == style.GetType() && basedOn is not ListStyle)
        {
            Word("sbasedon", StyleIndex(basedOn));
        }

        if (style is ParagraphStyle { Next: { IsInDocument: true } next })
        {
            Word("snext", StyleIndex(next));
        }

        Style? linked = style switch
        {
            ParagraphStyle paragraph => paragraph.Linked,
            CharacterStyle character => character.Linked,
            _ => null,
        };
        if (linked is { IsInDocument: true })
        {
            Word("slink", StyleIndex(linked));
        }

        if (style.IsPrimary)
        {
            Word("sqformat");
        }

        if (style.Priority is int priority)
        {
            Word("spriority", priority);
        }

        if (style.Hidden)
        {
            Word("ssemihidden");
        }
    }

    /// <summary>What the reader takes from a table style's own table formatting: its cell margins and indent.</summary>
    private void TableStyleWords(TableFormat f)
    {
        CellMargins(f);
        if (f.Indent is double indent)
        {
            Word("tblind", Twips(indent));
            Word("tblindtype", 3);
        }
    }

    // ---- Lists ----

    private void WriteLists()
    {
        if (document.Lists.Definitions.Count == 0)
        {
            return;
        }

        Dictionary<ListDefinition, int> ids = [];
        Destination("listtable");
        foreach (ListDefinition definition in document.Lists.Definitions)
        {
            int id = ids.Count + 1;
            ids[definition] = id;
            Open();
            Word("list");
            Word("listtemplateid", id);
            switch (definition.Kind)
            {
                case ListKind.SingleLevel:
                    Word("listsimple", 1);
                    break;
                case ListKind.HybridMultilevel:
                    Word("listhybrid");
                    break;
            }

            output.AppendLine();
            foreach (ListLevel level in definition.Levels.Take(9))
            {
                WriteListLevel(level);
            }

            Open();
            Word("listname");
            Space();
            Name(definition.Name ?? "");
            output.Append(';');
            Close();
            Word("listid", id);
            Close();
            output.AppendLine();
        }

        Close();
        Destination("listoverridetable");
        foreach (List list in document.Lists)
        {
            if (list.Id <= 0 || !ids.TryGetValue(list.Definition, out int id))
            {
                continue;
            }

            int last = -1;
            for (int i = 0; i < list.Overrides.Count && i < 9; i++)
            {
                if (list.Overrides[i].StartAt is not null || list.Overrides[i].Level is not null)
                {
                    last = i;
                }
            }

            Open();
            Word("listoverride");
            Word("listid", id);
            Word("listoverridecount", last + 1);
            for (int i = 0; i <= last; i++)
            {
                ListLevelOverride levelOverride = list.Overrides[i];
                Open();
                Word("lfolevel");
                if (levelOverride.StartAt is int start)
                {
                    Word("listoverridestartat");
                    Word("levelstartat", Math.Max(0, start));
                }

                if (levelOverride.Level is { } level)
                {
                    Word("listoverrideformat");
                    WriteListLevel(level);
                }

                Close();
            }

            Word("ls", list.Id);
            Close();
            output.AppendLine();
        }

        Close();
        output.AppendLine();
    }

    private void WriteListLevel(ListLevel level)
    {
        int code = level.NumberStyle switch
        {
            NumberStyle.UpperRoman => 1,
            NumberStyle.LowerRoman => 2,
            NumberStyle.UpperLetter => 3,
            NumberStyle.LowerLetter => 4,
            NumberStyle.Ordinal => 5,
            NumberStyle.CardinalText => 6,
            NumberStyle.OrdinalText => 7,
            NumberStyle.DecimalZero => 22,
            NumberStyle.Bullet => 23,
            NumberStyle.None => 255,
            _ => 0,
        };
        int justification = level.Alignment switch { ParagraphAlignment.Center => 1, ParagraphAlignment.Right => 2, _ => 0 };
        Open();
        Word("listlevel");
        Word("levelnfc", code);
        Word("levelnfcn", code);
        Word("leveljc", justification);
        Word("leveljcn", justification);
        Word("levelfollow", level.Suffix switch { ListLabelSuffix.Space => 1, ListLabelSuffix.Nothing => 2, _ => 0 });
        Word("levelstartat", Math.Max(0, level.StartAt));
        if (level.IsLegal)
        {
            Word("levellegal", 1);
        }

        if (level.RestartAfterLevel == 0)
        {
            Word("levelnorestart", 1);
        }

        LevelText(level.LevelText);
        ParagraphWords(level.ParagraphFormat);
        RunWords(level.RunFormat, null);
        Close();
        output.AppendLine();
    }

    /// <summary>
    /// A level's text as RTF holds it: <c>\leveltext</c> with the length first and each "%N" as the byte N - 1, and
    /// <c>\levelnumbers</c> with the places of those bytes. Other characters below a space are escapes, so that each counts once.
    /// </summary>
    private void LevelText(string text)
    {
        List<(char Char, bool Number)> chars = [];
        for (int i = 0; i < text.Length && chars.Count < 255; i++)
        {
            if (text[i] == '%' && i + 1 < text.Length && text[i + 1] is >= '1' and <= '9')
            {
                chars.Add(((char)(text[i + 1] - '1'), true));
                i++;
            }
            else
            {
                chars.Add((text[i], false));
            }
        }

        Open();
        Word("leveltext");
        Word("leveltemplateid", 0);
        Count(chars.Count);
        int start = 0;
        for (int i = 0; i <= chars.Count; i++)
        {
            if (i < chars.Count && !chars[i].Number && chars[i].Char >= ' ')
            {
                continue;
            }

            // The text between level numbers and control characters, surrogate pairs kept together.
            Text(new string([.. chars.Skip(start).Take(i - start).Select(c => c.Char)]));
            if (i < chars.Count)
            {
                if (chars[i].Number)
                {
                    Hex(chars[i].Char);
                }
                else
                {
                    Unicode(chars[i].Char);
                }
            }

            start = i + 1;
        }

        output.Append(';');
        Close();
        Open();
        Word("levelnumbers");
        for (int i = 0; i < chars.Count && i + 1 < ';'; i++)
        {
            if (chars[i].Number)
            {
                Hex(i + 1);
            }
        }

        output.Append(';');
        Close();

        // The length is a character; past 127 it is an escape, which reads the same in any code page.
        void Count(int count)
        {
            if (count < 0x80)
            {
                Hex(count);
            }
            else
            {
                Unicode((char)count);
            }
        }
    }

    /// <summary>A byte as a hexadecimal escape, <c>\'hh</c>.</summary>
    private void Hex(int value)
    {
        output.Append("\\'").Append(HexDigits[(value >> 4) & 0xF]).Append(HexDigits[value & 0xF]);
        pendingSpace = false;
    }

    // ---- Revision authors, information and settings ----

    private void WriteRevisionTable()
    {
        if (authorList.Count <= 1)
        {
            return;
        }

        Destination("revtbl");
        foreach (string author in authorList)
        {
            Open();
            Name(author);
            output.Append(';');
            Close();
        }

        Close();
        output.AppendLine();
    }

    private void WriteInfo()
    {
        DocumentProperties properties = document.Properties;
        Open();
        Word("info");
        InfoText("title", properties.Title);
        InfoText("subject", properties.Subject);
        InfoText("author", properties.Author);
        InfoText("keywords", properties.Keywords);
        InfoText("doccomm", properties.Comments);
        InfoText("category", properties.Category);
        InfoText("operator", properties.LastSavedBy);
        InfoDate("creatim", properties.Created);
        InfoDate("revtim", properties.Saved);
        InfoDate("printim", properties.Printed);
        Close();
        if (properties.CustomNames.Count > 0)
        {
            Destination("userprops");
            foreach (string name in properties.CustomNames)
            {
                Open();
                Word("propname");
                Space();
                Text(name);
                Close();
                Word("proptype", 30);
                Open();
                Word("staticval");
                Space();
                Text(properties.GetCustom(name) ?? "");
                Close();
            }

            Close();
        }

        output.AppendLine();
    }

    private void InfoText(string word, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Open();
            Word(word);
            Space();
            Text(value);
            Close();
        }
    }

    /// <summary>An information date, in UTC (the model keeps the moment; RTF has no time zone).</summary>
    private void InfoDate(string word, DateTimeOffset? date)
    {
        if (date is not { } value)
        {
            return;
        }

        DateTime utc = value.UtcDateTime;
        Open();
        Word(word);
        Word("yr", utc.Year);
        Word("mo", utc.Month);
        Word("dy", utc.Day);
        Word("hr", utc.Hour);
        Word("min", utc.Minute);
        Word("sec", utc.Second);
        Close();
    }

    private void WriteDocumentSettings()
    {
        DocumentSettings settings = document.Settings;
        if (document.Sections.Count > 0)
        {
            // The first section's page as the document's: what a reader without section words takes.
            PageSetup page = document.Sections[0].PageSetup;
            DocumentPageWord("paperw", page.PageWidth, positive: true);
            DocumentPageWord("paperh", page.PageHeight, positive: true);
            DocumentPageWord("margl", page.MarginLeft);
            DocumentPageWord("margr", page.MarginRight);
            DocumentPageWord("margt", page.MarginTop);
            DocumentPageWord("margb", page.MarginBottom);
            DocumentPageWord("gutter", page.Gutter);
        }

        if (Twips(settings.DefaultTabStop) > 0)
        {
            Word("deftab", Twips(settings.DefaultTabStop));
        }

        if (settings.EvenAndOddHeaders)
        {
            Word("facingp");
        }

        if (settings.MirrorMargins)
        {
            Word("margmirror");
        }

        if (settings.GutterAtTop)
        {
            Word("gutterprl");
        }

        if (settings.TrackRevisions)
        {
            Word("revisions");
        }

        Word("ftnstart", Math.Max(1, settings.FootnoteStartAt));
        Word("aftnstart", Math.Max(1, settings.EndnoteStartAt));
        Word("ftnn" + NoteNumbers(settings.FootnoteNumberStyle));
        Word("aftnn" + NoteNumbers(settings.EndnoteNumberStyle));
        WordCompatibility compatibility = settings.WordCompatibility;
        if (compatibility.DoNotBreakWrappedTables)
        {
            Word("nobrkwrptbl");
        }

        if (compatibility.SplitPageBreakAndParagraphMark)
        {
            Word("spltpgpar");
        }

        if (compatibility.DoNotUseIndentAsNumberingTabStop)
        {
            Word("noindnmbrts");
        }

        output.AppendLine();

        static string NoteNumbers(NumberStyle style) => style switch
        {
            NumberStyle.LowerLetter => "alc",
            NumberStyle.UpperLetter => "auc",
            NumberStyle.LowerRoman => "rlc",
            NumberStyle.UpperRoman => "ruc",
            _ => "ar",
        };
    }

    private void DocumentPageWord(string word, double? value, bool positive = false)
    {
        if (value is double points && (!positive || Twips(points) > 0))
        {
            Word(word, word is "margt" or "margb" ? Twips(points) : Math.Max(0, Twips(points)));
        }
    }

    /// <summary>The groups the reader kept before the body (Word's theme, latent styles, data store...), as they were.</summary>
    private void WriteKeptGroups()
    {
        if (options.KeepUnknownGroups && document.Extensions.Get<RtfExtension>(RtfExtension.Format)?.Groups is { } groups)
        {
            foreach (string markup in groups)
            {
                Raw(markup);
                output.AppendLine();
            }
        }
    }
}
