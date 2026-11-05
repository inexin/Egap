using Egap.Documents.Word;

namespace Egap.Documents.Tests;

/// <summary>The shared Word property stream: each property in Word's units, with Word's limits.</summary>
public sealed class WordPropertiesTests
{
    [Fact]
    public void RunPropertiesTakeWordsUnits()
    {
        RunFormat run = new();
        WordProperties.Set(run, WordRunProperty.Bold, 1);
        WordProperties.Set(run, WordRunProperty.Italic, 0);
        WordProperties.Set(run, WordRunProperty.Size, 25);
        WordProperties.Set(run, WordRunProperty.Position, -6);
        WordProperties.Set(run, WordRunProperty.Spacing, 30);
        WordProperties.Set(run, WordRunProperty.Kerning, -4);
        WordProperties.Set(run, WordRunProperty.Scale, 900);
        WordProperties.Set(run, WordRunProperty.Language, 0x0414);
        WordProperties.Set(run, WordRunProperty.Underline, (int)UnderlineStyle.Double);

        Assert.True(run.Bold);
        Assert.False(run.Italic);
        Assert.Equal(12.5, run.Size);
        Assert.Equal(-3, run.Position);
        Assert.Equal(1.5, run.Spacing);
        Assert.Equal(0, run.Kerning);
        Assert.Equal(600, run.Scale);
        Assert.Equal("nb-NO", run.Language);
        Assert.Equal(UnderlineStyle.Double, run.Underline);

        // Sizes stop at Word's largest, 1638 points; quarter points are RTF's \expnd.
        WordProperties.Set(run, WordRunProperty.Size, 100_000);
        WordProperties.Set(run, WordRunProperty.SpacingQuarterPoints, 6);
        Assert.Equal(1638, run.Size);
        Assert.Equal(1.5, run.Spacing);

        WordProperties.Set(run, WordRunProperty.RightToLeft, 1);
        Assert.True(run.RightToLeft);
        WordProperties.Set(run, WordRunProperty.RightToLeft, 0);
        Assert.False(run.RightToLeft);
    }

    [Fact]
    public void FontsGoToTheirSlots()
    {
        RunFormat run = new();
        WordProperties.SetFont(run, WordFontSlot.Latin, "Calibri");
        WordProperties.SetFont(run, WordFontSlot.EastAsia, "MS Mincho");
        WordProperties.SetFont(run, WordFontSlot.ComplexScript, "Arial");
        Assert.Equal(("Calibri", "Calibri", "MS Mincho", "Arial"), (run.FontAscii, run.FontHighAnsi, run.FontEastAsia, run.FontComplexScript));
    }

    [Fact]
    public void ParagraphPropertiesTakeTwipsAndClampSpacing()
    {
        ParagraphFormat paragraph = new();
        WordProperties.Set(paragraph, WordParagraphProperty.LeftIndent, 720);
        WordProperties.Set(paragraph, WordParagraphProperty.FirstLineIndent, -360);
        WordProperties.Set(paragraph, WordParagraphProperty.SpaceBefore, -240);
        WordProperties.Set(paragraph, WordParagraphProperty.OutlineLevel, 12);
        Assert.Equal(36, paragraph.LeftIndent);
        Assert.Equal(-18, paragraph.FirstLineIndent);
        Assert.Equal(0, paragraph.SpaceBefore);
        Assert.Equal(9, paragraph.OutlineLevel);
    }

    [Theory]
    [InlineData(0, false, null, null)]
    [InlineData(360, true, 1.5, LineSpacingRule.Multiple)]
    [InlineData(360, false, 18.0, LineSpacingRule.AtLeast)]
    [InlineData(-280, false, 14.0, LineSpacingRule.Exactly)]
    [InlineData(-280, true, 14.0, LineSpacingRule.Exactly)]
    public void LineSpacingIsWordsLspd(int value, bool multiple, double? spacing, LineSpacingRule? rule)
    {
        ParagraphFormat paragraph = new();
        WordProperties.SetLineSpacing(paragraph, value, multiple);
        Assert.Equal(spacing, paragraph.LineSpacing);
        Assert.Equal(rule, paragraph.LineSpacingRule);
    }

    [Fact]
    public void TabStopsReplaceTheirPlaceAndStopAtSixtyFour()
    {
        ParagraphFormat paragraph = new();
        WordProperties.AddTabStop(paragraph, new TabStop(36, TabAlignment.Left));
        WordProperties.AddTabStop(paragraph, new TabStop(36, TabAlignment.Right));
        Assert.Equal(TabAlignment.Right, Assert.Single(paragraph.TabStops!).Alignment);

        for (int i = 0; i < 100; i++)
        {
            WordProperties.AddTabStop(paragraph, new TabStop(100 + i, TabAlignment.Left));
        }

        Assert.Equal(WordProperties.MaxTabStops, paragraph.TabStops!.Count);
    }

    [Fact]
    public void FrameHeightsAndOffsetsFollowWord()
    {
        FramePosition frame = WordProperties.Set(new FramePosition(), WordFrameProperty.Height, -400);
        Assert.Equal((20.0, RowHeightRule.Exactly), (frame.Height, frame.HeightRule));
        frame = WordProperties.Set(frame, WordFrameProperty.HorizontalAlignment, (int)FloatingAlignment.Center);
        frame = WordProperties.Set(frame, WordFrameProperty.HorizontalOffset, 1440);
        Assert.Equal((72.0, FloatingAlignment.None), (frame.HorizontalOffset, frame.HorizontalAlignment));
        Assert.Equal(10, WordProperties.Set(frame, WordFrameProperty.DropCapLines, 40).DropCapLines);
    }

    [Fact]
    public void SectionMarginsKeepTheirSigns()
    {
        PageSetup setup = new();
        WordProperties.Set(setup, WordSectionProperty.MarginLeft, -100);
        WordProperties.Set(setup, WordSectionProperty.MarginTop, -1440);
        WordProperties.Set(setup, WordSectionProperty.ColumnCount, 99);
        Assert.Equal(0, setup.MarginLeft);
        Assert.Equal(-72, setup.MarginTop);
        Assert.Equal(45, setup.ColumnCount);
    }

    [Fact]
    public void RowDefinitionsCollectCellsUpToWordsLimit()
    {
        WordRowDefinition row = new();
        WordProperties.Set(row, WordTableProperty.RowHeight, -500);
        WordProperties.Set(row, WordTableProperty.Alignment, (int)TableAlignment.Center);
        WordProperties.Set(row, WordTableProperty.DefaultMarginLeft, 108);
        for (int i = 1; i <= 70; i++)
        {
            WordProperties.Set(row, WordTableProperty.CellMarginTop, -5);
            WordProperties.Set(row, WordTableProperty.CellRight, i * 100);
        }

        Assert.Equal((25.0, RowHeightRule.Exactly), (row.Row.Height, row.Row.HeightRule));
        Assert.Equal(TableAlignment.Center, row.Row.Alignment);
        Assert.Equal(TableAlignment.Center, row.Table.Alignment);
        Assert.Equal(5.4, row.Table.CellMarginLeft!.Value, 6);
        Assert.Equal(WordProperties.MaxCells, row.Cells.Count);
        Assert.Equal(0, row.Cells[0].Format.MarginTop);
        Assert.Equal(6300, row.Cells[^1].Right);
    }

    [Fact]
    public void ListLevelsTakeWordsCodes()
    {
        WordListLevel level = new();
        WordProperties.Set(level, WordListLevelProperty.NumberFormat, 23);
        WordProperties.Set(level, WordListLevelProperty.Alignment, 2);
        WordProperties.Set(level, WordListLevelProperty.Follow, 1);
        WordProperties.Set(level, WordListLevelProperty.StartAt, -3);
        Assert.Equal((NumberStyle.Bullet, ParagraphAlignment.Right, ListLabelSuffix.Space, 0), (level.Style, level.Alignment, level.Suffix, level.Start));

        ListDefinition definition = new Document().Lists.AddDefinition();
        level.FillLevel(definition.Levels[0]);
        Assert.Equal(NumberStyle.Bullet, definition.Levels[0].NumberStyle);
    }

    [Fact]
    public void StylesNotesAndFields()
    {
        WordStyleDefinition style = new();
        WordProperties.Set(style, WordStyleProperty.CharacterIndex, 12);
        WordProperties.Set(style, WordStyleProperty.Hidden, 1);
        Assert.Equal((StyleType.Character, 12, true), (style.Type, style.Index, style.Hidden));

        Document document = new();
        WordProperties.Set(document.Settings, WordNoteProperty.FootnoteNumberStyle, (int)NumberStyle.LowerRoman);
        Assert.Equal(NumberStyle.LowerRoman, document.Settings.FootnoteNumberStyle);

        FieldStart field = new(document);
        WordProperties.Set(field, WordFieldProperty.Locked, 1);
        Assert.True(field.IsLocked);
    }

    [Fact]
    public void ClearTakesAPropertyOffAndDirectionIsAToggle()
    {
        RunFormat run = new();
        WordProperties.Set(run, WordRunProperty.RightToLeft, 0);
        Assert.False(run.RightToLeft);
        WordProperties.Clear(run, WordRunProperty.RightToLeft);
        Assert.Null(run.RightToLeft);

        // Fractional twips and half-points, as DOCX may give them.
        WordProperties.Set(run, WordRunProperty.Size, 23.5);
        Assert.Equal(11.75, run.Size);
        WordProperties.Clear(run, WordRunProperty.Size);
        Assert.Null(run.Size);
        WordProperties.Set(run, WordRunProperty.Scale, 0);
        Assert.Equal(1, run.Scale);
    }

    [Theory]
    [InlineData(LineSpacingRule.Multiple, 360, 1.5)]
    [InlineData(LineSpacingRule.Multiple, -480, 2.0)]
    [InlineData(LineSpacingRule.Exactly, -280, 14.0)]
    [InlineData(LineSpacingRule.AtLeast, 0, 0.0)]
    public void LineSpacingWithItsRuleGivenApart(LineSpacingRule rule, double value, double spacing)
    {
        ParagraphFormat paragraph = new();
        WordProperties.SetLineSpacing(paragraph, rule, value);
        Assert.Equal((spacing, rule), (paragraph.LineSpacing, paragraph.LineSpacingRule));
    }

    [Fact]
    public void RowsAndCellsTakeTheirOwnFormats()
    {
        RowFormat row = new();
        WordProperties.Set(row, WordTableProperty.RowHeightAuto, 400);
        Assert.Equal((20.0, RowHeightRule.Auto), (row.Height, row.HeightRule));
        WordProperties.Set(row, WordTableProperty.RowHeight, -300);
        Assert.Equal((15.0, RowHeightRule.Exactly), (row.Height, row.HeightRule));
        WordProperties.Set(row, WordTableProperty.RowHidden, 1);
        WordProperties.Set(row, WordTableProperty.RowAlignment, (int)TableAlignment.Right);
        Assert.True(row.Hidden);
        Assert.Equal(TableAlignment.Right, row.Alignment);

        CellFormat cell = new();
        WordProperties.Set(cell, WordTableProperty.CellGridSpan, 1);
        Assert.Null(cell.GridSpan);
        WordProperties.Set(cell, WordTableProperty.CellGridSpan, 3);
        WordProperties.Set(cell, WordTableProperty.CellMarginLeft, -40);
        Assert.Equal((3, 0.0), (cell.GridSpan, cell.MarginLeft));

        TableFormat table = new();
        WordProperties.Set(table, WordTableProperty.RowBandSize, 0);
        WordProperties.Set(table, WordTableProperty.ColumnBandSize, 2);
        WordProperties.Set(table, WordTableProperty.Layout, (int)TableLayoutKind.Fixed);
        Assert.Equal((null, 2, TableLayoutKind.Fixed), (table.RowBandSize, table.ColumnBandSize, table.Layout));
    }

    [Fact]
    public void ListLevelsStylesAndFramesOnTheModel()
    {
        ListLevel level = new Document().Lists.AddDefinition().Levels[0];
        WordProperties.Set(level, WordListLevelProperty.NumberStyle, (int)NumberStyle.UpperRoman);
        WordProperties.Set(level, WordListLevelProperty.Follow, 2);
        WordProperties.Set(level, WordListLevelProperty.RestartAfter, 12);
        Assert.Equal((NumberStyle.UpperRoman, ListLabelSuffix.Nothing, 8), (level.NumberStyle, level.Suffix, level.RestartAfterLevel));

        Document document = new();
        Style style = document.Styles.AddParagraphStyle("Quote", "Quote");
        WordProperties.Set(style, WordStyleProperty.Priority, 29);
        WordProperties.Set(style, WordStyleProperty.Primary, 1);
        Assert.Equal((29, true), (style.Priority, style.IsPrimary));

        FramePosition frame = WordProperties.Set(new FramePosition(), WordFrameProperty.Height, 400);
        frame = WordProperties.Set(frame, WordFrameProperty.HeightRule, (int)RowHeightRule.Auto);
        Assert.Equal((20.0, RowHeightRule.Auto), (frame.Height, frame.HeightRule));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(0x0400, null)]
    [InlineData(0x0409, "en-US")]
    [InlineData(0x7FFF_FFFF, null)]
    public void LanguagesAreLcids(int lcid, string? tag) => Assert.Equal(tag, WordProperties.Language(lcid));

    [Fact]
    public void DatesAreDttms()
    {
        // 2026-11-05 09:30: minute 30, hour 9, day 5, month 11, year 126 since 1900.
        int dttm = 30 | (9 << 6) | (5 << 11) | (11 << 16) | (126 << 20);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 9, 30, 0, TimeSpan.Zero), WordProperties.Date(dttm));
        Assert.Null(WordProperties.Date(0));
        Assert.Null(WordProperties.Date((31 << 11) | (2 << 16) | (126 << 20)));
    }

    [Fact]
    public void ShadingPercentsPickTheirPattern()
    {
        Assert.Equal(ShadingPattern.Solid, WordProperties.Shade(null, percent: 10000).Pattern);
        Shading percent = WordProperties.Shade(null, percent: 2500);
        Assert.Equal((ShadingPattern.Percent, 25.0), (percent.Pattern, percent.Percent));
        Assert.Equal(ShadingPattern.Clear, WordProperties.Shade(null, percent: -5).Pattern);
    }
}
