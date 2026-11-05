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

    public static void Set(RunFormat run, WordRunProperty property, double value)
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
            case WordRunProperty.SpecialHidden:
                run.SpecialHidden = on;
                break;
            case WordRunProperty.ComplexScript:
                run.ComplexScript = on;
                break;
            case WordRunProperty.RightToLeft:
                run.RightToLeft = on;
                break;
            case WordRunProperty.Underline:
                run.Underline = (UnderlineStyle)(int)value;
                break;
            case WordRunProperty.Size:
                run.Size = Math.Clamp(value, 0, 3276) / 2.0;
                break;
            case WordRunProperty.SizeComplexScript:
                run.SizeComplexScript = Math.Clamp(value, 0, 3276) / 2.0;
                break;
            case WordRunProperty.Position:
                run.Position = value / 2.0;
                break;
            case WordRunProperty.VerticalPosition:
                run.VerticalPosition = (VerticalTextPosition)(int)value;
                break;
            case WordRunProperty.Spacing:
                run.Spacing = value / 20.0;
                break;
            case WordRunProperty.SpacingQuarterPoints:
                run.Spacing = value / 4.0;
                break;
            case WordRunProperty.Scale:
                run.Scale = Math.Clamp(value, 1, 600);
                break;
            case WordRunProperty.Kerning:
                run.Kerning = Math.Max(0, value) / 2.0;
                break;
            case WordRunProperty.Language:
                run.Language = Language((int)value);
                break;
            case WordRunProperty.LanguageEastAsia:
                run.LanguageEastAsia = Language((int)value);
                break;
            case WordRunProperty.LanguageBidi:
                run.LanguageBidi = Language((int)value);
                break;
            case WordRunProperty.Highlight:
                run.Highlight = (HighlightColor)(int)value;
                break;
        }
    }

    /// <summary>A language as a tag (<c>w:lang</c>), or null for none.</summary>
    public static void SetLanguage(RunFormat run, WordRunProperty property, string? tag)
    {
        switch (property)
        {
            case WordRunProperty.LanguageEastAsia:
                run.LanguageEastAsia = tag;
                break;
            case WordRunProperty.LanguageBidi:
                run.LanguageBidi = tag;
                break;
            default:
                run.Language = tag;
                break;
        }
    }

    /// <summary>Sets a paragraph's tab stops, as many as Word keeps.</summary>
    public static void SetTabStops(ParagraphFormat paragraph, IEnumerable<TabStop> stops)
    {
        paragraph.TabStops = [.. stops.Take(MaxTabStops)];
    }

    /// <summary>Takes a property off a run, as a value Word does not know does.</summary>
    public static void Clear(RunFormat run, WordRunProperty property)
    {
        switch (property)
        {
            case WordRunProperty.RightToLeft:
                run.RightToLeft = null;
                break;
            case WordRunProperty.Size:
                run.Size = null;
                break;
            case WordRunProperty.SizeComplexScript:
                run.SizeComplexScript = null;
                break;
            case WordRunProperty.Position:
                run.Position = null;
                break;
            case WordRunProperty.Spacing or WordRunProperty.SpacingQuarterPoints:
                run.Spacing = null;
                break;
            case WordRunProperty.Scale:
                run.Scale = null;
                break;
            case WordRunProperty.Kerning:
                run.Kerning = null;
                break;
            case WordRunProperty.Highlight:
                run.Highlight = null;
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

    public static void Set(ParagraphFormat paragraph, WordParagraphProperty property, double value)
    {
        bool on = value != 0;
        switch (property)
        {
            case WordParagraphProperty.Alignment:
                paragraph.Alignment = (ParagraphAlignment)(int)value;
                break;
            case WordParagraphProperty.Kashida:
                paragraph.Kashida = (KashidaLevel)(int)value;
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
                paragraph.Bidi = on;
                break;
            case WordParagraphProperty.OutlineLevel:
                paragraph.OutlineLevel = (int)Math.Clamp(value, 0, 9);
                break;
        }
    }

    /// <summary>Takes a property off a paragraph, as a value Word does not know does.</summary>
    public static void Clear(ParagraphFormat paragraph, WordParagraphProperty property)
    {
        switch (property)
        {
            case WordParagraphProperty.Alignment:
                paragraph.Alignment = null;
                break;
            case WordParagraphProperty.Kashida:
                paragraph.Kashida = null;
                break;
            case WordParagraphProperty.LeftIndent:
                paragraph.LeftIndent = null;
                break;
            case WordParagraphProperty.RightIndent:
                paragraph.RightIndent = null;
                break;
            case WordParagraphProperty.FirstLineIndent:
                paragraph.FirstLineIndent = null;
                break;
            case WordParagraphProperty.SpaceBefore:
                paragraph.SpaceBefore = null;
                break;
            case WordParagraphProperty.SpaceAfter:
                paragraph.SpaceAfter = null;
                break;
            case WordParagraphProperty.Bidi:
                paragraph.Bidi = null;
                break;
        }
    }

    /// <summary>
    /// Line spacing with its rule given apart (<c>w:spacing</c>): for <see cref="LineSpacingRule.Multiple"/> in 240ths of
    /// a line, otherwise in twips. Its size counts, not its sign.
    /// </summary>
    public static void SetLineSpacing(ParagraphFormat paragraph, LineSpacingRule rule, double value)
    {
        paragraph.LineSpacingRule = rule;
        paragraph.LineSpacing = rule == LineSpacingRule.Multiple ? Math.Abs(value / 240) : Math.Abs(Twips(value));
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
        else
        {
            SetLineSpacing(paragraph, multiple && value > 0 ? LineSpacingRule.Multiple : value < 0 ? LineSpacingRule.Exactly : LineSpacingRule.AtLeast, value);
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
    public static FramePosition Set(FramePosition frame, WordFrameProperty property, double value)
    {
        return property switch
        {
            WordFrameProperty.Width => frame with { Width = value > 0 ? Twips(value) : null },
            WordFrameProperty.Height => frame with
            {
                Height = value == 0 ? null : Math.Abs(Twips(value)),
                HeightRule = value < 0 ? RowHeightRule.Exactly : value > 0 ? RowHeightRule.AtLeast : RowHeightRule.Auto,
            },
            WordFrameProperty.HeightRule => frame with { HeightRule = (RowHeightRule)(int)value },
            WordFrameProperty.HorizontalAnchor => frame with { HorizontalAnchor = (HorizontalAnchor)(int)value },
            WordFrameProperty.VerticalAnchor => frame with { VerticalAnchor = (VerticalAnchor)(int)value },
            WordFrameProperty.HorizontalOffset => frame with { HorizontalOffset = Twips(value), HorizontalAlignment = FloatingAlignment.None },
            WordFrameProperty.VerticalOffset => frame with { VerticalOffset = Twips(value), VerticalAlignment = FloatingAlignment.None },
            WordFrameProperty.HorizontalAlignment => frame with { HorizontalAlignment = (FloatingAlignment)(int)value },
            WordFrameProperty.VerticalAlignment => frame with { VerticalAlignment = (FloatingAlignment)(int)value },
            WordFrameProperty.Distance => frame with { DistanceHorizontal = Twips(value), DistanceVertical = Twips(value) },
            WordFrameProperty.DistanceHorizontal => frame with { DistanceHorizontal = Twips(value) },
            WordFrameProperty.DistanceVertical => frame with { DistanceVertical = Twips(value) },
            WordFrameProperty.Wrap => frame with { Wrap = (FrameWrap)(int)value },
            WordFrameProperty.DropCapLines => frame with { DropCapLines = (int)Math.Clamp(value, 1, 10) },
            WordFrameProperty.DropCap => frame with { DropCap = (DropCap)(int)value },
            WordFrameProperty.AnchorLock => frame with { AnchorLock = value != 0 },
            _ => frame,
        };
    }

    // ---- Sections ----

    public static void Set(PageSetup setup, WordSectionProperty property, double value)
    {
        switch (property)
        {
            case WordSectionProperty.Start:
                setup.SectionStart = (SectionStart)(int)value;
                break;
            case WordSectionProperty.PageWidth:
                setup.PageWidth = Math.Max(0, Twips(value));
                break;
            case WordSectionProperty.PageHeight:
                setup.PageHeight = Math.Max(0, Twips(value));
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
                setup.Orientation = (PageOrientation)(int)value;
                break;
            case WordSectionProperty.TitlePage:
                setup.DifferentFirstPage = value != 0;
                break;
            case WordSectionProperty.ColumnCount:
                setup.ColumnCount = (int)Math.Clamp(value, 1, 45);
                break;
            case WordSectionProperty.ColumnSpacing:
                setup.ColumnSpacing = Math.Max(0, Twips(value));
                break;
            case WordSectionProperty.ColumnSeparator:
                setup.ColumnSeparator = value != 0;
                break;
            case WordSectionProperty.PageNumberStyle:
                setup.PageNumberStyle = (NumberStyle)(int)value;
                break;
            case WordSectionProperty.VerticalAlignment:
                setup.VerticalAlignment = (VerticalAlignment)(int)value;
                break;
            case WordSectionProperty.Bidi:
                setup.Bidi = value != 0;
                break;
            case WordSectionProperty.TextDirection:
                setup.TextDirection = (TextDirection)(int)value;
                break;
            case WordSectionProperty.PageNumberStart:
                setup.PageNumberStart = (int)value;
                break;
            case WordSectionProperty.EqualColumnWidths:
                setup.EqualColumnWidths = value != 0;
                break;
        }
    }

    /// <summary>Takes a property off a section, as a value Word does not know does.</summary>
    public static void Clear(PageSetup setup, WordSectionProperty property)
    {
        switch (property)
        {
            case WordSectionProperty.PageWidth:
                setup.PageWidth = null;
                break;
            case WordSectionProperty.PageHeight:
                setup.PageHeight = null;
                break;
            case WordSectionProperty.MarginLeft:
                setup.MarginLeft = null;
                break;
            case WordSectionProperty.MarginRight:
                setup.MarginRight = null;
                break;
            case WordSectionProperty.MarginTop:
                setup.MarginTop = null;
                break;
            case WordSectionProperty.MarginBottom:
                setup.MarginBottom = null;
                break;
            case WordSectionProperty.Gutter:
                setup.Gutter = null;
                break;
            case WordSectionProperty.HeaderDistance:
                setup.HeaderDistance = null;
                break;
            case WordSectionProperty.FooterDistance:
                setup.FooterDistance = null;
                break;
            case WordSectionProperty.ColumnSpacing:
                setup.ColumnSpacing = null;
                break;
            case WordSectionProperty.PageNumberStyle:
                setup.PageNumberStyle = null;
                break;
            case WordSectionProperty.PageNumberStart:
                setup.PageNumberStart = null;
                break;
        }
    }

    // ---- Tables ----

    /// <summary>A table-wide property: alignment, layout, indent, default cell margins, direction and the look.</summary>
    public static void Set(TableFormat table, WordTableProperty property, double value)
    {
        bool on = value != 0;
        switch (property)
        {
            case WordTableProperty.Alignment or WordTableProperty.TableAlignment:
                table.Alignment = (TableAlignment)(int)value;
                break;
            case WordTableProperty.CellSpacing or WordTableProperty.TableCellSpacing:
                table.CellSpacing = Twips(value);
                break;
            case WordTableProperty.Layout:
                table.Layout = (TableLayoutKind)(int)value;
                break;
            case WordTableProperty.RowBandSize:
                table.RowBandSize = value >= 1 ? (int)value : null;
                break;
            case WordTableProperty.ColumnBandSize:
                table.ColumnBandSize = value >= 1 ? (int)value : null;
                break;
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
                table.Bidi = on;
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

    /// <summary>Takes a property off a table, as a value Word does not know does.</summary>
    public static void Clear(TableFormat table, WordTableProperty property)
    {
        switch (property)
        {
            case WordTableProperty.Alignment or WordTableProperty.TableAlignment:
                table.Alignment = null;
                break;
            case WordTableProperty.CellSpacing or WordTableProperty.TableCellSpacing:
                table.CellSpacing = null;
                break;
            case WordTableProperty.Indent:
                table.Indent = null;
                break;
            case WordTableProperty.DefaultMarginLeft:
                table.CellMarginLeft = null;
                break;
            case WordTableProperty.DefaultMarginTop:
                table.CellMarginTop = null;
                break;
            case WordTableProperty.DefaultMarginRight:
                table.CellMarginRight = null;
                break;
            case WordTableProperty.DefaultMarginBottom:
                table.CellMarginBottom = null;
                break;
            case WordTableProperty.Bidi:
                table.Bidi = null;
                break;
            case WordTableProperty.LookFirstRow:
                table.LookFirstRow = null;
                break;
            case WordTableProperty.LookLastRow:
                table.LookLastRow = null;
                break;
            case WordTableProperty.LookFirstColumn:
                table.LookFirstColumn = null;
                break;
            case WordTableProperty.LookLastColumn:
                table.LookLastColumn = null;
                break;
            case WordTableProperty.LookNoRowBands:
                table.LookNoRowBands = null;
                break;
            case WordTableProperty.LookNoColumnBands:
                table.LookNoColumnBands = null;
                break;
        }
    }

    /// <summary>A row's property: its height, toggles, alignment and cell spacing.</summary>
    public static void Set(RowFormat row, WordTableProperty property, double value)
    {
        bool on = value != 0;
        switch (property)
        {
            case WordTableProperty.RowHeight:
                row.Height = value == 0 ? null : Math.Abs(Twips(value));
                row.HeightRule = value == 0 ? RowHeightRule.Auto : value > 0 ? RowHeightRule.AtLeast : RowHeightRule.Exactly;
                break;
            case WordTableProperty.RowHeightExactly:
                row.Height = Math.Max(0, Twips(value));
                row.HeightRule = RowHeightRule.Exactly;
                break;
            case WordTableProperty.RowHeightAtLeast:
                row.Height = Math.Max(0, Twips(value));
                row.HeightRule = RowHeightRule.AtLeast;
                break;
            case WordTableProperty.RowHeightAuto:
                row.Height = Math.Max(0, Twips(value));
                row.HeightRule = RowHeightRule.Auto;
                break;
            case WordTableProperty.HeaderRow:
                row.IsHeader = on;
                break;
            case WordTableProperty.CantSplit:
                row.CantSplit = on;
                break;
            case WordTableProperty.RowHidden:
                row.Hidden = on;
                break;
            case WordTableProperty.Alignment or WordTableProperty.RowAlignment:
                row.Alignment = (TableAlignment)(int)value;
                break;
            case WordTableProperty.CellSpacing or WordTableProperty.RowCellSpacing:
                row.CellSpacing = Twips(value);
                break;
        }
    }

    /// <summary>Takes a property off a row, as a value Word does not know does.</summary>
    public static void Clear(RowFormat row, WordTableProperty property)
    {
        switch (property)
        {
            case WordTableProperty.RowHeight or WordTableProperty.RowHeightExactly or WordTableProperty.RowHeightAtLeast or WordTableProperty.RowHeightAuto:
                row.Height = null;
                break;
            case WordTableProperty.Alignment or WordTableProperty.RowAlignment:
                row.Alignment = null;
                break;
            case WordTableProperty.CellSpacing or WordTableProperty.RowCellSpacing:
                row.CellSpacing = null;
                break;
        }
    }

    /// <summary>A cell's property: merging, alignment, margins, toggles and text direction.</summary>
    public static void Set(CellFormat cell, WordTableProperty property, double value)
    {
        bool on = value != 0;
        switch (property)
        {
            case WordTableProperty.CellVerticalMerge:
                cell.VerticalMerge = (VerticalMerge)(int)value;
                break;
            case WordTableProperty.CellGridSpan:
                cell.GridSpan = value > 1 ? (int)value : null;
                break;
            case WordTableProperty.CellVerticalAlignment:
                cell.VerticalAlignment = (VerticalAlignment)(int)value;
                break;
            case WordTableProperty.CellMarginLeft:
                cell.MarginLeft = Twips(Math.Max(0, value));
                break;
            case WordTableProperty.CellMarginTop:
                cell.MarginTop = Twips(Math.Max(0, value));
                break;
            case WordTableProperty.CellMarginRight:
                cell.MarginRight = Twips(Math.Max(0, value));
                break;
            case WordTableProperty.CellMarginBottom:
                cell.MarginBottom = Twips(Math.Max(0, value));
                break;
            case WordTableProperty.CellNoWrap:
                cell.NoWrap = on;
                break;
            case WordTableProperty.CellFitText:
                cell.FitText = on;
                break;
            case WordTableProperty.CellHideMark:
                cell.HideMark = on;
                break;
            case WordTableProperty.CellTextDirection:
                cell.TextDirection = (TextDirection)(int)value;
                break;
        }
    }

    /// <summary>Takes a property off a cell, as a value Word does not know does.</summary>
    public static void Clear(CellFormat cell, WordTableProperty property)
    {
        switch (property)
        {
            case WordTableProperty.CellVerticalAlignment:
                cell.VerticalAlignment = null;
                break;
            case WordTableProperty.CellMarginLeft:
                cell.MarginLeft = null;
                break;
            case WordTableProperty.CellMarginTop:
                cell.MarginTop = null;
                break;
            case WordTableProperty.CellMarginRight:
                cell.MarginRight = null;
                break;
            case WordTableProperty.CellMarginBottom:
                cell.MarginBottom = null;
                break;
            case WordTableProperty.CellTextDirection:
                cell.TextDirection = null;
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
            case WordTableProperty.RowHeight or WordTableProperty.RowHeightExactly or WordTableProperty.RowHeightAtLeast or WordTableProperty.RowHeightAuto
                or WordTableProperty.HeaderRow or WordTableProperty.CantSplit or WordTableProperty.RowHidden or WordTableProperty.RowAlignment
                or WordTableProperty.RowCellSpacing:
                Set(definition.Row, property, value);
                break;
            case WordTableProperty.Alignment or WordTableProperty.CellSpacing:
                Set(definition.Row, property, value);
                Set(definition.Table, property, value);
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
            case WordTableProperty.Style:
                definition.Style = value;
                break;
            case WordTableProperty.HorizontalAnchor:
                definition.Position = (definition.Position ?? new TablePosition()) with { HorizontalAnchor = (HorizontalAnchor)(int)value };
                break;
            case WordTableProperty.VerticalAnchor:
                definition.Position = (definition.Position ?? new TablePosition()) with { VerticalAnchor = (VerticalAnchor)(int)value };
                break;
            case WordTableProperty.HorizontalOffset:
                definition.Position = (definition.Position ?? new TablePosition()) with { HorizontalOffset = Twips(value), HorizontalAlignment = FloatingAlignment.None };
                break;
            case WordTableProperty.VerticalOffset:
                definition.Position = (definition.Position ?? new TablePosition()) with { VerticalOffset = Twips(value), VerticalAlignment = FloatingAlignment.None };
                break;
            case WordTableProperty.HorizontalAlignment:
                definition.Position = (definition.Position ?? new TablePosition()) with { HorizontalAlignment = (FloatingAlignment)(int)value };
                break;
            case WordTableProperty.VerticalAlignment:
                definition.Position = (definition.Position ?? new TablePosition()) with { VerticalAlignment = (FloatingAlignment)(int)value };
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
            case WordTableProperty.CellWidthUnit:
                cell.WidthUnit = value;
                break;
            case WordTableProperty.CellWidth:
                cell.Width = value;
                break;
            case WordTableProperty.CellVerticalMerge or WordTableProperty.CellGridSpan or WordTableProperty.CellVerticalAlignment
                or WordTableProperty.CellMarginLeft or WordTableProperty.CellMarginTop or WordTableProperty.CellMarginRight
                or WordTableProperty.CellMarginBottom or WordTableProperty.CellNoWrap or WordTableProperty.CellFitText
                or WordTableProperty.CellHideMark or WordTableProperty.CellTextDirection:
                Set(cell.Format, property, value);
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

    /// <summary>A list level's property, on a list definition's level.</summary>
    public static void Set(ListLevel level, WordListLevelProperty property, int value)
    {
        switch (property)
        {
            case WordListLevelProperty.NumberFormat:
                level.NumberStyle = NumberStyleOf(value);
                break;
            case WordListLevelProperty.NumberStyle:
                level.NumberStyle = (NumberStyle)value;
                break;
            case WordListLevelProperty.Alignment:
                level.Alignment = value switch { 1 => ParagraphAlignment.Center, 2 => ParagraphAlignment.Right, _ => ParagraphAlignment.Left };
                break;
            case WordListLevelProperty.Follow:
                level.Suffix = value switch { 1 => ListLabelSuffix.Space, 2 => ListLabelSuffix.Nothing, _ => ListLabelSuffix.Tab };
                break;
            case WordListLevelProperty.StartAt:
                level.StartAt = Math.Max(0, value);
                break;
            case WordListLevelProperty.Legal:
                level.IsLegal = value != 0;
                break;
            case WordListLevelProperty.NoRestart:
                if (value != 0)
                {
                    level.RestartAfterLevel = 0;
                }

                break;
            case WordListLevelProperty.RestartAfter:
                level.RestartAfterLevel = Math.Clamp(value, 0, 8);
                break;
        }
    }

    /// <summary>A style's own setting: hidden, in the gallery, or its sort priority.</summary>
    public static void Set(Style style, WordStyleProperty property, int value)
    {
        switch (property)
        {
            case WordStyleProperty.Hidden:
                style.Hidden = value != 0;
                break;
            case WordStyleProperty.Primary:
                style.IsPrimary = value != 0;
                break;
            case WordStyleProperty.Priority:
                style.Priority = value;
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
                settings.FootnoteNumberStyle = (NumberStyle)(int)value;
                break;
            case WordNoteProperty.EndnoteNumberStyle:
                settings.EndnoteNumberStyle = (NumberStyle)(int)value;
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
    public static double Twips(double value) => value / 20.0;

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
