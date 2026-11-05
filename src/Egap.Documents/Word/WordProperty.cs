namespace Egap.Documents.Word;

// The properties of Word's model as its file formats name them: RTF's control words, DOC's sprms ([MS-DOC] 2.6) and
// DOCX's elements (ECMA-376 Part 1, 17.3 to 17.9) all say these, in Word's own units. Each reader turns its syntax into
// these and WordProperties puts them on the model, so the three readers agree on what a property means.

/// <summary>A character property and its operand (<see cref="WordProperties.Set(RunFormat, WordRunProperty, double)"/>).</summary>
internal enum WordRunProperty : byte
{
    /// <summary>Bold; operand 0 or 1. The toggles that follow work the same.</summary>
    Bold,
    Italic,
    BoldComplexScript,
    ItalicComplexScript,
    Strikethrough,
    DoubleStrikethrough,
    AllCaps,
    SmallCaps,
    Hidden,
    Outline,
    Shadow,
    Emboss,
    Imprint,
    NoProof,
    SpecialHidden,

    /// <summary>The run's text is complex script (sprmCFComplexScripts, <c>w:cs</c>).</summary>
    ComplexScript,

    /// <summary>Right to left; operand 0 or 1.</summary>
    RightToLeft,

    /// <summary>An <see cref="UnderlineStyle"/>.</summary>
    Underline,

    /// <summary>Size in half-points (sprmCHps, <c>\fs</c>, <c>w:sz</c>), 0 to 3276.</summary>
    Size,

    /// <summary>The complex-script size in half-points.</summary>
    SizeComplexScript,

    /// <summary>The raise or lower in half-points, negative for lowered.</summary>
    Position,

    /// <summary>A <see cref="VerticalTextPosition"/>.</summary>
    VerticalPosition,

    /// <summary>Letter spacing in twips (sprmCDxaSpace).</summary>
    Spacing,

    /// <summary>Letter spacing in quarter points (<c>\expnd</c>).</summary>
    SpacingQuarterPoints,

    /// <summary>Width scale in percent, 1 to 600.</summary>
    Scale,

    /// <summary>The smallest size kerned, in half-points.</summary>
    Kerning,

    /// <summary>A language as a Windows LCID; the next two the same.</summary>
    Language,
    LanguageEastAsia,
    LanguageBidi,

    /// <summary>A <see cref="HighlightColor"/>.</summary>
    Highlight,
}

/// <summary>A colour property of a run (<see cref="WordProperties.Set(RunFormat, WordRunColor, DocumentColor?)"/>).</summary>
internal enum WordRunColor : byte
{
    Color,
    UnderlineColor,
}

/// <summary>Which font of a run a font name sets.</summary>
internal enum WordFontSlot : byte
{
    ComplexScript,
    Ascii,
    HighAnsi,
    EastAsia,

    /// <summary>ASCII and high ANSI together, as <c>\f</c> sets them.</summary>
    Latin,
}

/// <summary>A paragraph property and its operand (<see cref="WordProperties.Set(ParagraphFormat, WordParagraphProperty, double)"/>).</summary>
internal enum WordParagraphProperty : byte
{
    /// <summary>A <see cref="ParagraphAlignment"/>.</summary>
    Alignment,

    /// <summary>A <see cref="KashidaLevel"/>.</summary>
    Kashida,

    /// <summary>Left indent in twips; right and first-line the same.</summary>
    LeftIndent,
    RightIndent,
    FirstLineIndent,

    /// <summary>Space before in twips, never below 0; after the same.</summary>
    SpaceBefore,
    SpaceAfter,

    /// <summary>Toggles, 0 or 1.</summary>
    SpaceBeforeAuto,
    SpaceAfterAuto,
    KeepTogether,
    KeepWithNext,
    PageBreakBefore,
    WidowControl,
    ContextualSpacing,
    SuppressLineNumbers,
    SuppressAutoHyphens,
    SnapToGrid,
    MirrorIndents,

    /// <summary>Right to left; operand 0 or 1.</summary>
    Bidi,

    /// <summary>The outline level, 0 to 9 (9 is body text).</summary>
    OutlineLevel,
}

/// <summary>A positioned frame's property (<see cref="WordProperties.Set(FramePosition, WordFrameProperty, double)"/>).</summary>
internal enum WordFrameProperty : byte
{
    /// <summary>Width in twips, 0 for automatic.</summary>
    Width,

    /// <summary>Height in twips: negative is exact, positive at least, 0 automatic.</summary>
    Height,

    /// <summary>A <see cref="RowHeightRule"/> for the height, given apart from it (<c>w:framePr</c>'s hRule).</summary>
    HeightRule,

    /// <summary>A <see cref="HorizontalAnchor"/>; vertical the same.</summary>
    HorizontalAnchor,
    VerticalAnchor,

    /// <summary>Offset in twips, which clears the alignment; vertical the same.</summary>
    HorizontalOffset,
    VerticalOffset,

    /// <summary>A <see cref="FloatingAlignment"/>; vertical the same.</summary>
    HorizontalAlignment,
    VerticalAlignment,

    /// <summary>Distance from the text in twips: both, horizontal and vertical.</summary>
    Distance,
    DistanceHorizontal,
    DistanceVertical,

    /// <summary>A <see cref="FrameWrap"/>.</summary>
    Wrap,

    /// <summary>Lines a drop cap spans, 1 to 10.</summary>
    DropCapLines,

    /// <summary>A <see cref="DropCap"/>.</summary>
    DropCap,

    /// <summary>The anchor is locked.</summary>
    AnchorLock,
}

/// <summary>A section property (<see cref="WordProperties.Set(PageSetup, WordSectionProperty, double)"/>).</summary>
internal enum WordSectionProperty : byte
{
    /// <summary>A <see cref="SectionStart"/>.</summary>
    Start,

    /// <summary>Page width and height in twips, above 0.</summary>
    PageWidth,
    PageHeight,

    /// <summary>Side margins in twips, never below 0.</summary>
    MarginLeft,
    MarginRight,

    /// <summary>Top and bottom margins in twips; negative fixes the margin.</summary>
    MarginTop,
    MarginBottom,

    /// <summary>Gutter and header and footer distances in twips, never below 0.</summary>
    Gutter,
    HeaderDistance,
    FooterDistance,

    /// <summary>A <see cref="PageOrientation"/>.</summary>
    Orientation,

    /// <summary>A different first page; operand 0 or 1.</summary>
    TitlePage,

    /// <summary>Number of columns, 1 to 45.</summary>
    ColumnCount,

    /// <summary>Space between columns in twips, never below 0.</summary>
    ColumnSpacing,

    /// <summary>A line between columns.</summary>
    ColumnSeparator,

    /// <summary>A <see cref="NumberStyle"/> for page numbers.</summary>
    PageNumberStyle,

    /// <summary>A <see cref="VerticalAlignment"/> of the text on the page.</summary>
    VerticalAlignment,

    /// <summary>Right to left.</summary>
    Bidi,

    /// <summary>A <see cref="TextDirection"/> of the text on the page.</summary>
    TextDirection,

    /// <summary>The first page number.</summary>
    PageNumberStart,

    /// <summary>Columns of equal width; operand 0 or 1.</summary>
    EqualColumnWidths,
}

/// <summary>A table, row or cell property (<see cref="WordProperties.Set(WordRowDefinition, WordTableProperty, int)"/>).</summary>
internal enum WordTableProperty : byte
{
    /// <summary>Half the space between cells' text, in twips (<c>\trgaph</c>, sprmTDxaGapHalf).</summary>
    Gap,

    /// <summary>The left edge of the first cell, in twips.</summary>
    Left,

    /// <summary>Row height in twips: negative is exact, positive at least, 0 automatic.</summary>
    RowHeight,

    /// <summary>Row height in twips with its rule given apart (<c>w:trHeight</c>): exactly, at least and automatic.</summary>
    RowHeightExactly,
    RowHeightAtLeast,
    RowHeightAuto,

    /// <summary>Toggles of the row: header row, can't split.</summary>
    HeaderRow,
    CantSplit,

    /// <summary>The row is hidden.</summary>
    RowHidden,

    /// <summary>A <see cref="TableAlignment"/> for the row and its table; the next two for the table or the row alone.</summary>
    Alignment,
    TableAlignment,
    RowAlignment,

    /// <summary>The table fits its contents.</summary>
    AutoFit,

    /// <summary>The preferred width's unit (Word's ftsWidth: 0 none, 1 auto, 2 fiftieths of a percent, 3 twips) and value.</summary>
    WidthUnit,
    Width,

    /// <summary>Space between cells in twips, for the row and its table; the next two for the table or the row alone.</summary>
    CellSpacing,
    TableCellSpacing,
    RowCellSpacing,

    /// <summary>A <see cref="TableLayoutKind"/>.</summary>
    Layout,

    /// <summary>Rows and columns in a style band, above 0.</summary>
    RowBandSize,
    ColumnBandSize,

    /// <summary>Table indent in twips.</summary>
    Indent,

    /// <summary>The table's default cell margins in twips, never below 0.</summary>
    DefaultMarginLeft,
    DefaultMarginTop,
    DefaultMarginRight,
    DefaultMarginBottom,

    /// <summary>The table style's index.</summary>
    Style,

    /// <summary>Right to left; operand 0 or 1.</summary>
    Bidi,

    /// <summary>The table style options Word calls the look.</summary>
    LookFirstRow,
    LookLastRow,
    LookFirstColumn,
    LookLastColumn,
    LookNoRowBands,
    LookNoColumnBands,

    /// <summary>A floating table's anchors (<see cref="HorizontalAnchor"/>, <see cref="VerticalAnchor"/>).</summary>
    HorizontalAnchor,
    VerticalAnchor,

    /// <summary>A floating table's offsets in twips, which clear its alignments.</summary>
    HorizontalOffset,
    VerticalOffset,

    /// <summary>A floating table's alignments (<see cref="FloatingAlignment"/>).</summary>
    HorizontalAlignment,
    VerticalAlignment,

    /// <summary>A floating table's distances from the text in twips.</summary>
    DistanceLeft,
    DistanceRight,
    DistanceTop,
    DistanceBottom,

    // The pending cell's properties, before its right edge ends it.

    /// <summary>The first and following cells of a horizontal merge.</summary>
    CellMergeFirst,
    CellMerged,

    /// <summary>A <see cref="VerticalMerge"/>.</summary>
    CellVerticalMerge,

    /// <summary>Grid columns the cell spans; above 1 to span.</summary>
    CellGridSpan,

    /// <summary>A <see cref="VerticalAlignment"/>.</summary>
    CellVerticalAlignment,

    /// <summary>The cell's preferred width unit and value, as the table's.</summary>
    CellWidthUnit,
    CellWidth,

    /// <summary>Cell margins in twips, never below 0.</summary>
    CellMarginLeft,
    CellMarginTop,
    CellMarginRight,
    CellMarginBottom,

    /// <summary>Toggles of the cell.</summary>
    CellNoWrap,
    CellFitText,
    CellHideMark,

    /// <summary>A <see cref="TextDirection"/>.</summary>
    CellTextDirection,

    /// <summary>The cell's right edge in twips, which ends the pending cell.</summary>
    CellRight,
}

/// <summary>A list level's property (<see cref="WordProperties.Set(WordListLevel, WordListLevelProperty, int)"/>).</summary>
internal enum WordListLevelProperty : byte
{
    /// <summary>Word's number format code (nfc: 0 decimal, 1 upper Roman, 23 bullet, 255 none, ...).</summary>
    NumberFormat,

    /// <summary>Word's justification code (0 left, 1 centre, 2 right).</summary>
    Alignment,

    /// <summary>What follows the number (0 tab, 1 space, 2 nothing).</summary>
    Follow,

    /// <summary>The first number, never below 0.</summary>
    StartAt,

    /// <summary>Legal numbering, and no restart after a higher level.</summary>
    Legal,
    NoRestart,

    /// <summary>A <see cref="Documents.NumberStyle"/>, for formats that name more styles than nfc codes.</summary>
    NumberStyle,

    /// <summary>The level after which numbering restarts, 0 to 8.</summary>
    RestartAfter,
}

/// <summary>
/// A style's property (<see cref="WordProperties.Set(WordStyleDefinition, WordStyleProperty, int)"/>, or
/// <see cref="WordProperties.Set(Style, WordStyleProperty, int)"/> for a style that exists already).
/// </summary>
internal enum WordStyleProperty : byte
{
    /// <summary>The style's index, as a paragraph, character or table style.</summary>
    ParagraphIndex,
    CharacterIndex,
    TableIndex,

    /// <summary>The indexes of the style it is based on, the next paragraph's and the linked one.</summary>
    BasedOn,
    Next,
    Link,

    /// <summary>Hidden from the style lists; operand 0 or 1.</summary>
    Hidden,

    /// <summary>In the quick style gallery.</summary>
    Primary,

    /// <summary>The sort priority.</summary>
    Priority,
}

/// <summary>A footnote or endnote setting of the document (<see cref="WordProperties.Set(DocumentSettings, WordNoteProperty, int)"/>).</summary>
internal enum WordNoteProperty : byte
{
    /// <summary>The first number, from 1.</summary>
    FootnoteStart,
    EndnoteStart,

    /// <summary>A <see cref="NumberStyle"/>.</summary>
    FootnoteNumberStyle,
    EndnoteNumberStyle,
}

/// <summary>A field's property (<see cref="WordProperties.Set(FieldStart, WordFieldProperty, int)"/>).</summary>
internal enum WordFieldProperty : byte
{
    /// <summary>The result is not updated; operand 0 or 1.</summary>
    Locked,

    /// <summary>The result needs updating.</summary>
    Dirty,
}
