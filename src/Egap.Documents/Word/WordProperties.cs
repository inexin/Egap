using System.Globalization;

namespace Egap.Documents.Word;

/// <summary>
/// The shared Word property stream: puts a property, named as Word's formats name it and given in Word's own units, on
/// the model. The RTF reader sends its control words here (and the DOC and DOCX readers their sprms and elements), so a
/// property means the same in every Word format: the same units, limits and defaults.
/// </summary>
/// <remarks>
/// Operands are integers as the formats store them: twips, half-points, LCIDs, Word's codes, model enum values, and 0 or
/// 1 for toggles. What a reader needs a table for (a colour or font index) it resolves first and passes the value.
/// </remarks>
internal static class WordProperties
{
    // ---- Characters ----

    public static void Set(RunFormat run, WordRunProperty property, int value)
    {
        bool on = value != 0;
        switch (property)
        {
            case WordRunProperty.Bold:
                run.Bold = on;
                break;
            case WordRunProperty.Italic:
                run.Italic = on;
                break;
            case WordRunProperty.BoldComplexScript:
                run.BoldComplexScript = on;
                break;
            case WordRunProperty.ItalicComplexScript:
                run.ItalicComplexScript = on;
                break;
            case WordRunProperty.Strikethrough:
                run.Strikethrough = on;
                break;
            case WordRunProperty.DoubleStrikethrough:
                run.DoubleStrikethrough = on;
                break;
            case WordRunProperty.AllCaps:
                run.AllCaps = on;
                break;
            case WordRunProperty.SmallCaps:
                run.SmallCaps = on;
                break;
            case WordRunProperty.Hidden:
                run.Hidden = on;
                break;
            case WordRunProperty.Outline:
                run.Outline = on;
                break;
            case WordRunProperty.Shadow:
                run.Shadow = on;
                break;
            case WordRunProperty.Emboss:
                run.Emboss = on;
                break;
            case WordRunProperty.Imprint:
                run.Imprint = on;
                break;
            case WordRunProperty.NoProof:
                run.NoProof = on;
                break;
            case WordRunProperty.RightToLeft:
                run.RightToLeft = on ? true : null;
                break;
            case WordRunProperty.Underline:
                run.Underline = (UnderlineStyle)value;
                break;
            case WordRunProperty.Size:
                run.Size = Math.Min(value, 3276) / 2.0;
                break;
            case WordRunProperty.SizeComplexScript:
                run.SizeComplexScript = Math.Min(value, 3276) / 2.0;
                break;
            case WordRunProperty.Position:
                run.Position = value / 2.0;
                break;
            case WordRunProperty.VerticalPosition:
                run.VerticalPosition = (VerticalTextPosition)value;
                break;
            case WordRunProperty.Spacing:
                run.Spacing = value / 20.0;
                break;
            case WordRunProperty.SpacingQuarterPoints:
                run.Spacing = value / 4.0;
                break;
            case WordRunProperty.Scale:
                run.Scale = Math.Min(value, 600);
                break;
            case WordRunProperty.Kerning:
                run.Kerning = Math.Max(0, value) / 2.0;
                break;
            case WordRunProperty.Language:
                run.Language = Language(value);
                break;
            case WordRunProperty.LanguageEastAsia:
                run.LanguageEastAsia = Language(value);
                break;
            case WordRunProperty.LanguageBidi:
                run.LanguageBidi = Language(value);
                break;
            case WordRunProperty.Highlight:
                run.Highlight = (HighlightColor)value;
                break;
        }
    }

    public static void Set(RunFormat run, WordRunColor property, DocumentColor? color)
    {
        if (property == WordRunColor.Color)
        {
            run.Color = color;
        }
        else
        {
            run.UnderlineColor = color;
        }
    }

    public static void SetFont(RunFormat run, WordFontSlot slot, string name)
    {
        switch (slot)
        {
            case WordFontSlot.Latin:
                run.FontAscii = name;
                run.FontHighAnsi = name;
                break;
            case WordFontSlot.Ascii:
                run.FontAscii = name;
                break;
            case WordFontSlot.HighAnsi:
                run.FontHighAnsi = name;
                break;
            case WordFontSlot.EastAsia:
                run.FontEastAsia = name;
                break;
            default:
                run.FontComplexScript = name;
                break;
        }
    }

    // ---- Paragraphs ----

    public static void Set(ParagraphFormat paragraph, WordParagraphProperty property, int value)
    {
        bool on = value != 0;
        switch (property)
        {
            case WordParagraphProperty.Alignment:
                paragraph.Alignment = (ParagraphAlignment)value;
                break;
            case WordParagraphProperty.Kashida:
                paragraph.Kashida = (KashidaLevel)value;
                break;
            case WordParagraphProperty.LeftIndent:
                paragraph.LeftIndent = Twips(value);
                break;
            case WordParagraphProperty.RightIndent:
                paragraph.RightIndent = Twips(value);
                break;
            case WordParagraphProperty.FirstLineIndent:
                paragraph.FirstLineIndent = Twips(value);
                break;
            case WordParagraphProperty.SpaceBefore:
                paragraph.SpaceBefore = Math.Max(0, Twips(value));
                break;
            case WordParagraphProperty.SpaceAfter:
                paragraph.SpaceAfter = Math.Max(0, Twips(value));
                break;
            case WordParagraphProperty.SpaceBeforeAuto:
                paragraph.SpaceBeforeAuto = on;
                break;
            case WordParagraphProperty.SpaceAfterAuto:
                paragraph.SpaceAfterAuto = on;
                break;
            case WordParagraphProperty.KeepTogether:
                paragraph.KeepTogether = on;
                break;
            case WordParagraphProperty.KeepWithNext:
                paragraph.KeepWithNext = on;
                break;
            case WordParagraphProperty.PageBreakBefore:
                paragraph.PageBreakBefore = on;
                break;
            case WordParagraphProperty.WidowControl:
                paragraph.WidowControl = on;
                break;
            case WordParagraphProperty.ContextualSpacing:
                paragraph.ContextualSpacing = on;
                break;
            case WordParagraphProperty.SuppressLineNumbers:
                paragraph.SuppressLineNumbers = on;
                break;
            case WordParagraphProperty.SuppressAutoHyphens:
                paragraph.SuppressAutoHyphens = on;
                break;
            case WordParagraphProperty.SnapToGrid:
                paragraph.SnapToGrid = on;
                break;
            case WordParagraphProperty.MirrorIndents:
                paragraph.MirrorIndents = on;
                break;
            case WordParagraphProperty.Bidi:
                paragraph.Bidi = on ? true : null;
                break;
            case WordParagraphProperty.OutlineLevel:
                paragraph.OutlineLevel = Math.Clamp(value, 0, 9);
                break;
        }
    }

    /// <summary>
    /// Line spacing as Word stores it (LSPD, <c>\sl</c> and <c>\slmult</c>): 0 for single; with <paramref name="multiple"/>
    /// and a positive value, 240ths of a line; otherwise twips, negative for exactly and positive for at least.
    /// </summary>
    public static void SetLineSpacing(ParagraphFormat paragraph, int value, bool multiple)
    {
        if (value == 0)
        {
            paragraph.LineSpacing = null;
            paragraph.LineSpacingRule = null;
        }
        else if (multiple && value > 0)
        {
            paragraph.LineSpacing = value / 240.0;
            paragraph.LineSpacingRule = LineSpacingRule.Multiple;
        }
        else
        {
            paragraph.LineSpacing = Math.Abs(Twips(value));
            paragraph.LineSpacingRule = value < 0 ? LineSpacingRule.Exactly : LineSpacingRule.AtLeast;
        }
    }

    /// <summary>The most tab stops a paragraph keeps, as Word does.</summary>
    public const int MaxTabStops = 64;

    /// <summary>Adds a tab stop, replacing one at the same place; past <see cref="MaxTabStops"/> a new place is ignored.</summary>
    public static void AddTabStop(ParagraphFormat paragraph, TabStop stop)
    {
        if (paragraph.TabStops is { Count: >= MaxTabStops } stops && !stops.Any(t => t.Position == stop.Position))
        {
            return;
        }

        paragraph.TabStops = [.. (paragraph.TabStops ?? []).Where(t => t.Position != stop.Position), stop];
    }

    /// <summary>A positioned frame with one property changed.</summary>
    public static FramePosition Set(FramePosition frame, WordFrameProperty property, int value)
    {
        return property switch
        {
            WordFrameProperty.Width => frame with { Width = value > 0 ? Twips(value) : null },
            WordFrameProperty.Height => frame with
            {
                Height = value == 0 ? null : Math.Abs(Twips(value)),
                HeightRule = value < 0 ? RowHeightRule.Exactly : value > 0 ? RowHeightRule.AtLeast : RowHeightRule.Auto,
            },
            WordFrameProperty.HorizontalAnchor => frame with { HorizontalAnchor = (HorizontalAnchor)value },
            WordFrameProperty.VerticalAnchor => frame with { VerticalAnchor = (VerticalAnchor)value },
            WordFrameProperty.HorizontalOffset => frame with { HorizontalOffset = Twips(value), HorizontalAlignment = FloatingAlignment.None },
            WordFrameProperty.VerticalOffset => frame with { VerticalOffset = Twips(value), VerticalAlignment = FloatingAlignment.None },
            WordFrameProperty.HorizontalAlignment => frame with { HorizontalAlignment = (FloatingAlignment)value },
            WordFrameProperty.VerticalAlignment => frame with { VerticalAlignment = (FloatingAlignment)value },
            WordFrameProperty.Distance => frame with { DistanceHorizontal = Twips(value), DistanceVertical = Twips(value) },
            WordFrameProperty.DistanceHorizontal => frame with { DistanceHorizontal = Twips(value) },
            WordFrameProperty.DistanceVertical => frame with { DistanceVertical = Twips(value) },
            WordFrameProperty.Wrap => frame with { Wrap = (FrameWrap)value },
            WordFrameProperty.DropCapLines => frame with { DropCapLines = Math.Clamp(value, 1, 10) },
            WordFrameProperty.DropCap => frame with { DropCap = (DropCap)value },
            WordFrameProperty.AnchorLock => frame with { AnchorLock = value != 0 },
            _ => frame,
        };
    }

    // ---- Sections ----

    public static void Set(PageSetup setup, WordSectionProperty property, int value)
    {
        switch (property)
        {
            case WordSectionProperty.Start:
                setup.SectionStart = (SectionStart)value;
                break;
            case WordSectionProperty.PageWidth:
                setup.PageWidth = Twips(value);
                break;
            case WordSectionProperty.PageHeight:
                setup.PageHeight = Twips(value);
                break;
            case WordSectionProperty.MarginLeft:
                setup.MarginLeft = Math.Max(0, Twips(value));
                break;
            case WordSectionProperty.MarginRight:
                setup.MarginRight = Math.Max(0, Twips(value));
                break;
            case WordSectionProperty.MarginTop:
                setup.MarginTop = Twips(value);
                break;
            case WordSectionProperty.MarginBottom:
                setup.MarginBottom = Twips(value);
                break;
            case WordSectionProperty.Gutter:
                setup.Gutter = Math.Max(0, Twips(value));
                break;
            case WordSectionProperty.HeaderDistance:
                setup.HeaderDistance = Math.Max(0, Twips(value));
                break;
            case WordSectionProperty.FooterDistance:
                setup.FooterDistance = Math.Max(0, Twips(value));
                break;
            case WordSectionProperty.Orientation:
                setup.Orientation = (PageOrientation)value;
                break;
            case WordSectionProperty.TitlePage:
                setup.DifferentFirstPage = value != 0;
                break;
            case WordSectionProperty.ColumnCount:
                setup.ColumnCount = Math.Clamp(value, 1, 45);
                break;
            case WordSectionProperty.ColumnSpacing:
                setup.ColumnSpacing = Math.Max(0, Twips(value));
                break;
            case WordSectionProperty.ColumnSeparator:
                setup.ColumnSeparator = value != 0;
                break;
            case WordSectionProperty.PageNumberStyle:
                setup.PageNumberStyle = (NumberStyle)value;
                break;
            case WordSectionProperty.VerticalAlignment:
                setup.VerticalAlignment = (VerticalAlignment)value;
                break;
            case WordSectionProperty.Bidi:
                setup.Bidi = value != 0;
                break;
        }
    }

    // ---- Tables ----

    /// <summary>A table-wide property: indent, default cell margins, direction and the look.</summary>
    public static void Set(TableFormat table, WordTableProperty property, int value)
    {
        bool on = value != 0;
        switch (property)
        {
            case WordTableProperty.Indent:
                table.Indent = Twips(value);
                break;
            case WordTableProperty.DefaultMarginLeft:
                table.CellMarginLeft = Math.Max(0, Twips(value));
                break;
            case WordTableProperty.DefaultMarginTop:
                table.CellMarginTop = Math.Max(0, Twips(value));
                break;
            case WordTableProperty.DefaultMarginRight:
                table.CellMarginRight = Math.Max(0, Twips(value));
                break;
            case WordTableProperty.DefaultMarginBottom:
                table.CellMarginBottom = Math.Max(0, Twips(value));
                break;
            case WordTableProperty.Bidi:
                table.Bidi = on ? true : null;
                break;
            case WordTableProperty.LookFirstRow:
                table.LookFirstRow = on;
                break;
            case WordTableProperty.LookLastRow:
                table.LookLastRow = on;
                break;
            case WordTableProperty.LookFirstColumn:
                table.LookFirstColumn = on;
                break;
            case WordTableProperty.LookLastColumn:
                table.LookLastColumn = on;
                break;
            case WordTableProperty.LookNoRowBands:
                table.LookNoRowBands = on;
                break;
            case WordTableProperty.LookNoColumnBands:
                table.LookNoColumnBands = on;
                break;
        }
    }

    /// <summary>A property of a row definition: of its row, its table, its position or its pending cell.</summary>
    public static void Set(WordRowDefinition definition, WordTableProperty property, int value)
    {
        bool on = value != 0;
        WordCellDefinition cell = definition.Pending;
        switch (property)
        {
            case WordTableProperty.Gap:
                definition.Gap = value;
                break;
            case WordTableProperty.Left:
                definition.Left = value;
                break;
            case WordTableProperty.RowHeight:
                definition.Row.Height = value == 0 ? null : Twips(Math.Abs(value));
                definition.Row.HeightRule = value == 0 ? RowHeightRule.Auto : value > 0 ? RowHeightRule.AtLeast : RowHeightRule.Exactly;
                break;
            case WordTableProperty.HeaderRow:
                definition.Row.IsHeader = on;
                break;
            case WordTableProperty.CantSplit:
                definition.Row.CantSplit = on;
                break;
            case WordTableProperty.Alignment:
                definition.Row.Alignment = (TableAlignment)value;
                definition.Table.Alignment = (TableAlignment)value;
                break;
            case WordTableProperty.AutoFit:
                definition.AutoFit = on;
                break;
            case WordTableProperty.WidthUnit:
                definition.WidthUnit = value;
                break;
            case WordTableProperty.Width:
                definition.Width = value;
                break;
            case WordTableProperty.CellSpacing:
                definition.Table.CellSpacing = Twips(value);
                definition.Row.CellSpacing = Twips(value);
                break;
            case WordTableProperty.Style:
                definition.Style = value;
                break;
            case WordTableProperty.HorizontalAnchor:
                definition.Position = (definition.Position ?? new TablePosition()) with { HorizontalAnchor = (HorizontalAnchor)value };
                break;
            case WordTableProperty.VerticalAnchor:
                definition.Position = (definition.Position ?? new TablePosition()) with { VerticalAnchor = (VerticalAnchor)value };
                break;
            case WordTableProperty.HorizontalOffset:
                definition.Position = (definition.Position ?? new TablePosition()) with { HorizontalOffset = Twips(value), HorizontalAlignment = FloatingAlignment.None };
                break;
            case WordTableProperty.VerticalOffset:
                definition.Position = (definition.Position ?? new TablePosition()) with { VerticalOffset = Twips(value), VerticalAlignment = FloatingAlignment.None };
                break;
            case WordTableProperty.HorizontalAlignment:
                definition.Position = (definition.Position ?? new TablePosition()) with { HorizontalAlignment = (FloatingAlignment)value };
                break;
            case WordTableProperty.VerticalAlignment:
                definition.Position = (definition.Position ?? new TablePosition()) with { VerticalAlignment = (FloatingAlignment)value };
                break;
            case WordTableProperty.DistanceLeft:
                definition.Position = (definition.Position ?? new TablePosition()) with { DistanceLeft = Twips(value) };
                break;
            case WordTableProperty.DistanceRight:
                definition.Position = (definition.Position ?? new TablePosition()) with { DistanceRight = Twips(value) };
                break;
            case WordTableProperty.DistanceTop:
                definition.Position = (definition.Position ?? new TablePosition()) with { DistanceTop = Twips(value) };
                break;
            case WordTableProperty.DistanceBottom:
                definition.Position = (definition.Position ?? new TablePosition()) with { DistanceBottom = Twips(value) };
                break;
            case WordTableProperty.CellMergeFirst:
                cell.MergeFirst = on;
                break;
            case WordTableProperty.CellMerged:
                cell.Merged = on;
                break;
            case WordTableProperty.CellVerticalMerge:
                cell.Format.VerticalMerge = (VerticalMerge)value;
                break;
            case WordTableProperty.CellVerticalAlignment:
                cell.Format.VerticalAlignment = (VerticalAlignment)value;
                break;
            case WordTableProperty.CellWidthUnit:
                cell.WidthUnit = value;
                break;
            case WordTableProperty.CellWidth:
                cell.Width = value;
                break;
            case WordTableProperty.CellMarginLeft:
                cell.Format.MarginLeft = Twips(Math.Max(0, value));
                break;
            case WordTableProperty.CellMarginTop:
                cell.Format.MarginTop = Twips(Math.Max(0, value));
                break;
            case WordTableProperty.CellMarginRight:
                cell.Format.MarginRight = Twips(Math.Max(0, value));
                break;
            case WordTableProperty.CellMarginBottom:
                cell.Format.MarginBottom = Twips(Math.Max(0, value));
                break;
            case WordTableProperty.CellNoWrap:
                cell.Format.NoWrap = on;
                break;
            case WordTableProperty.CellFitText:
                cell.Format.FitText = on;
                break;
            case WordTableProperty.CellHideMark:
                cell.Format.HideMark = on;
                break;
            case WordTableProperty.CellTextDirection:
                cell.Format.TextDirection = (TextDirection)value;
                break;
            case WordTableProperty.CellRight:
                cell.Right = value;
                if (definition.Cells.Count < MaxCells)
                {
                    definition.Cells.Add(cell);
                }

                definition.Pending = new WordCellDefinition();
                break;
            default:
                Set(definition.Table, property, value);
                break;
        }
    }

    /// <summary>The most cells a row keeps, as Word does.</summary>
    public const int MaxCells = 63;

    // ---- Lists, styles, notes and fields ----

    public static void Set(WordListLevel level, WordListLevelProperty property, int value)
    {
        switch (property)
        {
            case WordListLevelProperty.NumberFormat:
                level.Style = NumberStyleOf(value);
                break;
            case WordListLevelProperty.Alignment:
                level.Alignment = value switch { 1 => ParagraphAlignment.Center, 2 => ParagraphAlignment.Right, _ => ParagraphAlignment.Left };
                break;
            case WordListLevelProperty.Follow:
                level.Suffix = value switch { 1 => ListLabelSuffix.Space, 2 => ListLabelSuffix.Nothing, _ => ListLabelSuffix.Tab };
                break;
            case WordListLevelProperty.StartAt:
                level.Start = Math.Max(0, value);
                break;
            case WordListLevelProperty.Legal:
                level.Legal = value != 0;
                break;
            case WordListLevelProperty.NoRestart:
                level.NoRestart = value != 0;
                break;
        }
    }

    public static void Set(WordStyleDefinition style, WordStyleProperty property, int value)
    {
        switch (property)
        {
            case WordStyleProperty.ParagraphIndex:
                style.Type = StyleType.Paragraph;
                style.Index = value;
                break;
            case WordStyleProperty.CharacterIndex:
                style.Type = StyleType.Character;
                style.Index = value;
                break;
            case WordStyleProperty.TableIndex:
                style.Type = StyleType.Table;
                style.Index = value;
                break;
            case WordStyleProperty.BasedOn:
                style.BasedOn = value;
                break;
            case WordStyleProperty.Next:
                style.Next = value;
                break;
            case WordStyleProperty.Link:
                style.Link = value;
                break;
            case WordStyleProperty.Hidden:
                style.Hidden = value != 0;
                break;
            case WordStyleProperty.Primary:
                style.Primary = value != 0;
                break;
            case WordStyleProperty.Priority:
                style.Priority = value;
                break;
        }
    }

    public static void Set(DocumentSettings settings, WordNoteProperty property, int value)
    {
        switch (property)
        {
            case WordNoteProperty.FootnoteStart:
                settings.FootnoteStartAt = value;
                break;
            case WordNoteProperty.EndnoteStart:
                settings.EndnoteStartAt = value;
                break;
            case WordNoteProperty.FootnoteNumberStyle:
                settings.FootnoteNumberStyle = (NumberStyle)value;
                break;
            case WordNoteProperty.EndnoteNumberStyle:
                settings.EndnoteNumberStyle = (NumberStyle)value;
                break;
        }
    }

    public static void Set(FieldStart field, WordFieldProperty property, int value)
    {
        if (property == WordFieldProperty.Locked)
        {
            field.IsLocked = value != 0;
        }
        else
        {
            field.IsDirty = value != 0;
        }
    }

    // ---- Values ----

    /// <summary>Twips (twentieths of a point) in points.</summary>
    public static double Twips(int value) => value / 20.0;

    /// <summary>
    /// A shading with one part changed: a percentage of pattern colour in hundredths (0 to 10000, as <c>\shading</c> and
    /// SHD's ipat give it), a fill, a pattern colour or a pattern.
    /// </summary>
    public static Shading Shade(Shading? shading, int? percent = null, DocumentColor? fill = null, DocumentColor? patternColor = null, ShadingPattern? pattern = null)
    {
        Shading s = shading ?? new Shading(ShadingPattern.Clear);
        if (percent is int value)
        {
            double share = Math.Clamp(value, 0, 10000) / 100.0;
            s = s.Pattern is ShadingPattern.Clear or ShadingPattern.Percent or ShadingPattern.Solid
                ? s with { Pattern = share <= 0 ? ShadingPattern.Clear : share >= 100 ? ShadingPattern.Solid : ShadingPattern.Percent, Percent = share }
                : s with { Percent = share };
        }

        if (fill is { } f)
        {
            s = s with { Fill = f };
        }

        if (patternColor is { } c)
        {
            s = s with { PatternColor = c };
        }

        if (pattern is { } kind)
        {
            s = s with { Pattern = kind };
        }

        return s;
    }

    /// <summary>Word's DTTM: minutes, hours, day, month and years since 1900 packed into 32 bits; null when not a date.</summary>
    public static DateTimeOffset? Date(int dttm)
    {
        uint value = (uint)dttm;
        int minute = (int)(value & 0x3F);
        int hour = (int)((value >> 6) & 0x1F);
        int day = (int)((value >> 11) & 0x1F);
        int month = (int)((value >> 16) & 0xF);
        int year = 1900 + (int)((value >> 20) & 0x1FF);
        if (value == 0 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59)
        {
            return null;
        }

        return new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);
    }

    /// <summary>A Windows LCID as a language tag; null for none, the neutral ones and unknown ones.</summary>
    public static string? Language(int lcid)
    {
        if (lcid is <= 0 or 1024 or 0x0400 or 0x7F or >= 0x10000)
        {
            return null;
        }

        try
        {
            string name = CultureInfo.GetCultureInfo(lcid).Name;
            return name.Length == 0 ? null : name;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Word's number format code (nfc, [MS-DOC] 2.9.172 and <c>\levelnfc</c>) as a number style.</summary>
    public static NumberStyle NumberStyleOf(int code) => code switch
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
}
