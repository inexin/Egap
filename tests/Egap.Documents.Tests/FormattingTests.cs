using Egami.Pixels;

namespace Egap.Documents.Tests;

/// <summary>
/// Formats, styles, the theme and effective formatting (Phase 6 Build Plan task 3, design Q5): Word's order of document
/// defaults, table style, paragraph style, character style and direct formatting, toggles, tabs and the per-paragraph cache.
/// </summary>
public class FormattingTests
{
    [Fact]
    public void NewDocumentHasWordsDefaults()
    {
        Document doc = new();
        Assert.Equal(0, doc.Version);
        Run run = doc.AddSection().Body.AddParagraph().AddRun("text");

        ResolvedRunFormat format = run.GetEffectiveFormat();
        ResolvedParagraphFormat paragraph = run.Paragraph!.GetEffectiveFormat();

        Assert.Equal(11, format.Size);
        Assert.Equal("Calibri", format.FontAscii);
        Assert.Equal(new Rgba8(0, 0, 0, 255), format.TextColor);
        Assert.Equal("en-US", format.Language);
        Assert.Equal(8, paragraph.SpaceAfter);
        Assert.Equal(259 / 240.0, paragraph.LineSpacing);
        Assert.Equal("Normal", doc.Styles.DefaultParagraphStyle!.Id);
        Assert.True(doc.Styles.DefaultParagraphStyle.IsDefault);
        Assert.Equal(5.4, doc.Styles.DefaultTableStyle!.TableFormat.CellMarginLeft);
    }

    [Fact]
    public void HeadingStyleGivesWordsHeadingOne()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph("Title");
        p.Style = (ParagraphStyle)doc.Styles.AddBuiltIn(BuiltInStyle.Heading1);

        ResolvedRunFormat run = p.Runs.Single().GetEffectiveFormat();
        ResolvedParagraphFormat format = p.GetEffectiveFormat();

        Assert.Equal(16, run.Size);
        Assert.Equal("Calibri Light", run.FontAscii);
        AssertClose(Rgba8.FromHex("#2F5496"), run.TextColor);
        Assert.Equal(12, format.SpaceBefore);
        Assert.Equal(0, format.SpaceAfter);
        Assert.Equal(0, format.OutlineLevel);
        Assert.True(format.KeepWithNext);
        Assert.Equal("heading 1", p.Style.Name);
        Assert.Same(doc.Styles.DefaultParagraphStyle, p.Style.BasedOn);
        Assert.Same(p.Style, doc.Styles.AddBuiltIn(BuiltInStyle.Heading1));
    }

    [Fact]
    public void LevelsApplyInWordsOrder()
    {
        Document doc = new();
        ParagraphStyle paragraphStyle = doc.Styles.AddParagraphStyle("Body");
        paragraphStyle.RunFormat.Size = 14;
        paragraphStyle.RunFormat.Color = DocumentColor.FromHex("#0000FF");
        CharacterStyle characterStyle = doc.Styles.AddCharacterStyle("Red");
        characterStyle.RunFormat.Color = DocumentColor.FromHex("#FF0000");
        Paragraph p = doc.AddSection().Body.AddParagraph();
        p.Style = paragraphStyle;
        Run plain = p.AddRun("a");
        Run styled = p.AddRun("b");
        styled.Style = characterStyle;
        Run direct = p.AddRun("c", new RunFormat { Color = DocumentColor.FromHex("#00FF00"), Size = 9 });
        direct.Style = characterStyle;

        Assert.Equal((14.0, "#0000FFFF"), (plain.GetEffectiveFormat().Size, plain.GetEffectiveFormat().TextColor.ToHex()));
        Assert.Equal((14.0, "#FF0000FF"), (styled.GetEffectiveFormat().Size, styled.GetEffectiveFormat().TextColor.ToHex()));
        Assert.Equal((9.0, "#00FF00FF"), (direct.GetEffectiveFormat().Size, direct.GetEffectiveFormat().TextColor.ToHex()));
    }

    [Fact]
    public void BasedOnChainFillsWhatAStyleLeavesOut()
    {
        Document doc = new();
        ParagraphStyle parent = doc.Styles.AddParagraphStyle("Parent");
        parent.ParagraphFormat.Alignment = ParagraphAlignment.Center;
        parent.ParagraphFormat.SpaceBefore = 6;
        ParagraphStyle child = doc.Styles.AddParagraphStyle("Child");
        child.BasedOn = parent;
        child.ParagraphFormat.SpaceBefore = 18;
        Paragraph p = doc.AddSection().Body.AddParagraph();
        p.Style = child;

        Assert.Equal(ParagraphAlignment.Center, p.GetEffectiveFormat().Alignment);
        Assert.Equal(18, p.GetEffectiveFormat().SpaceBefore);
        Assert.Equal([child, parent], child.BasedOnChain());
    }

    [Fact]
    public void TogglesFromStylesCombineByExclusiveOr()
    {
        Document doc = new();
        ParagraphStyle boldParagraph = doc.Styles.AddParagraphStyle("BoldPara");
        boldParagraph.RunFormat.Bold = true;
        CharacterStyle boldCharacter = doc.Styles.AddCharacterStyle("BoldChar");
        boldCharacter.RunFormat.Bold = true;
        Paragraph p = doc.AddSection().Body.AddParagraph();
        p.Style = boldParagraph;
        Run fromParagraph = p.AddRun("a");
        Run both = p.AddRun("b");
        both.Style = boldCharacter;
        Run direct = p.AddRun("c");
        direct.Style = boldCharacter;
        direct.Format.Bold = true;

        Assert.True(fromParagraph.GetEffectiveFormat().Bold);
        Assert.False(both.GetEffectiveFormat().Bold);
        Assert.True(direct.GetEffectiveFormat().Bold);
    }

    [Fact]
    public void ThemeFontAtTheSameLevelWinsAndANameLaterReplacesIt()
    {
        Document doc = new();
        Paragraph p = doc.AddSection().Body.AddParagraph();
        Run themed = p.AddRun("a", new RunFormat { FontAscii = "Arial", ThemeFontAscii = ThemeFont.MajorLatin });
        Run named = p.AddRun("b", new RunFormat { FontName = "Arial" });

        Assert.Equal("Calibri Light", themed.GetEffectiveFormat().FontAscii);
        Assert.Equal("Arial", named.GetEffectiveFormat().FontAscii);
        Assert.Equal("Arial", named.GetEffectiveFormat().FontEastAsia);

        doc.Theme.MajorLatinFont = "Aptos Display";
        Assert.Equal("Aptos Display", themed.GetEffectiveFormat().FontAscii);
    }

    [Fact]
    public void TabStopsAddUpAndClearRemoves()
    {
        Document doc = new();
        ParagraphStyle style = doc.Styles.AddParagraphStyle("Tabs");
        style.ParagraphFormat.TabStops = [new TabStop(72), new TabStop(144, TabAlignment.Right, TabLeader.Dot)];
        Paragraph p = doc.AddSection().Body.AddParagraph();
        p.Style = style;
        p.Format.TabStops = [new TabStop(72, TabAlignment.Clear), new TabStop(36, TabAlignment.Center)];

        Assert.Equal([new TabStop(36, TabAlignment.Center), new TabStop(144, TabAlignment.Right, TabLeader.Dot)], p.GetEffectiveFormat().TabStops);
    }

    [Fact]
    public void TableStyleRegionsApplyByPosition()
    {
        Document doc = new();
        TableStyle style = doc.Styles.AddTableStyle("Banded");
        style.BasedOn = doc.Styles.AddBuiltIn(BuiltInStyle.TableGrid);
        style[TableStyleRegion.FirstRow].RunFormat.Bold = true;
        style[TableStyleRegion.FirstRow].CellFormat.Shading = Shading.Solid(DocumentColor.FromHex("#4472C4"));
        style[TableStyleRegion.OddRowBand].CellFormat.Shading = Shading.Solid(DocumentColor.FromHex("#D9E2F3"));
        style[TableStyleRegion.TopLeftCell].RunFormat.Italic = true;
        Table table = doc.AddSection().Body.AddTable(4, 2);
        table.Style = style;
        Run header = table.Rows[0].Cells[0].Paragraphs.Single().AddRun("h");
        Run headerRight = table.Rows[0].Cells[1].Paragraphs.Single().AddRun("h2");
        Run body = table.Rows[1].Cells[0].Paragraphs.Single().AddRun("b");

        Assert.True(header.GetEffectiveFormat().Bold);
        Assert.True(header.GetEffectiveFormat().Italic);
        Assert.False(headerRight.GetEffectiveFormat().Italic);
        Assert.False(body.GetEffectiveFormat().Bold);
        Assert.Equal("#4472C4", table.Rows[0].Cells[1].GetEffectiveFormat().Shading!.Fill.ToString());
        Assert.Equal("#D9E2F3", table.Rows[1].Cells[0].GetEffectiveFormat().Shading!.Fill.ToString());
        Assert.Null(table.Rows[2].Cells[0].GetEffectiveFormat().Shading);
        Assert.Equal("#D9E2F3", table.Rows[3].Cells[0].GetEffectiveFormat().Shading!.Fill.ToString());
        Assert.Equal(BorderStyle.Single, table.GetEffectiveFormat().BorderInsideHorizontal!.Style);
        Assert.Equal(0, table.Rows[1].Cells[0].Paragraphs.Single().GetEffectiveFormat().SpaceAfter);

        table.Format.LookFirstRow = false;
        Assert.False(header.GetEffectiveFormat().Bold);
        Assert.Equal("#D9E2F3", table.Rows[0].Cells[0].GetEffectiveFormat().Shading!.Fill.ToString());
    }

    [Fact]
    public void ResultsFollowChangesThroughTheCache()
    {
        Document doc = new();
        ParagraphStyle style = doc.Styles.AddParagraphStyle("S");
        Paragraph p = doc.AddSection().Body.AddParagraph();
        p.Style = style;
        Run run = p.AddRun("x");
        Assert.Same(run.GetEffectiveFormat(), run.GetEffectiveFormat());

        style.RunFormat.Size = 20;
        Assert.Equal(20, run.GetEffectiveFormat().Size);

        run.Format.Size = 7;
        Assert.Equal(7, run.GetEffectiveFormat().Size);

        doc.Styles.DefaultParagraphFormat.SpaceAfter = 3;
        Assert.Equal(3, p.GetEffectiveFormat().SpaceAfter);

        doc.Styles.Remove(style);
        run.Format.Size = null;
        Assert.Equal(11, run.GetEffectiveFormat().Size);
        Assert.False(style.IsInDocument);
    }

    [Fact]
    public void InsertingARowMovesTheBands()
    {
        Document doc = new();
        TableStyle style = doc.Styles.AddTableStyle("Bands");
        style[TableStyleRegion.EvenRowBand].RunFormat.Italic = true;
        Table table = doc.AddSection().Body.AddTable(3, 1);
        table.Style = style;
        Run last = table.Rows[2].Cells[0].Paragraphs.Single().AddRun("r");
        Assert.True(last.GetEffectiveFormat().Italic);

        Row inserted = new(doc);
        inserted.AddCell();
        table.Rows.Insert(1, inserted);

        Assert.False(last.GetEffectiveFormat().Italic);
    }

    [Fact]
    public void StylesRefuseLoopsAndMixedTypes()
    {
        Document doc = new();
        ParagraphStyle a = doc.Styles.AddParagraphStyle("A");
        ParagraphStyle b = doc.Styles.AddParagraphStyle("B");
        b.BasedOn = a;

        Assert.Throws<ArgumentException>(() => a.BasedOn = b);
        Assert.Throws<ArgumentException>(() => a.BasedOn = a);
        Assert.Throws<ArgumentException>(() => a.BasedOn = doc.Styles.AddCharacterStyle("C"));
        Assert.Throws<ArgumentException>(() => doc.Styles.AddParagraphStyle("A"));
        Assert.Throws<ArgumentException>(() => a.BasedOn = new Document().Styles.AddParagraphStyle("X"));
        Assert.Throws<ArgumentException>(() => doc.AddSection().Body.AddParagraph().Style = new Document().Styles.DefaultParagraphStyle);
    }

    [Fact]
    public void RemovingAStyleRelinksTheStylesBasedOnIt()
    {
        Document doc = new();
        ParagraphStyle a = doc.Styles.AddParagraphStyle("A");
        ParagraphStyle b = doc.Styles.AddParagraphStyle("B");
        ParagraphStyle c = doc.Styles.AddParagraphStyle("C");
        b.BasedOn = a;
        c.BasedOn = b;

        Assert.True(doc.Styles.Remove(b));

        Assert.Same(a, c.BasedOn);
        Assert.Null(doc.Styles["B"]);
        Assert.False(doc.Styles.Remove(b));
    }

    [Fact]
    public void ImportBringsStylesAndUsesTheTargetsOfTheSameId()
    {
        Document source = new();
        ParagraphStyle custom = source.Styles.AddParagraphStyle("Custom");
        custom.BasedOn = (ParagraphStyle)source.Styles.AddBuiltIn(BuiltInStyle.Heading2);
        custom.RunFormat.Size = 30;
        Paragraph p = source.AddSection().Body.AddParagraph("x");
        p.Style = custom;
        Document target = new();
        ParagraphStyle heading2 = (ParagraphStyle)target.Styles.AddBuiltIn(BuiltInStyle.Heading2);
        heading2.RunFormat.Color = DocumentColor.FromHex("#123456");

        Paragraph imported = target.Import(p);
        target.AddSection().Body.Blocks.Add(imported);

        Assert.NotSame(custom, imported.Style);
        Assert.Same(target, imported.Style!.Document);
        Assert.Same(heading2, imported.Style.BasedOn);
        Assert.Equal(30, imported.Runs.Single().GetEffectiveFormat().Size);
        Assert.Equal("#123456FF", imported.Runs.Single().GetEffectiveFormat().TextColor.ToHex());
        Assert.Same(imported.Style, target.Import(p).Style);
    }

    [Fact]
    public void DocumentCloneCopiesStylesAndTheme()
    {
        Document doc = new();
        ParagraphStyle style = doc.Styles.AddParagraphStyle("S");
        style.RunFormat.Size = 15;
        doc.Theme.Colors[ThemeColor.Accent1] = Rgba8.FromHex("#112233");
        doc.AddSection().Body.AddParagraph("x").Style = style;

        Document copy = doc.Clone();
        style.RunFormat.Size = 99;

        Paragraph p = copy.Sections[0].Body.Paragraphs.Single();
        Assert.NotSame(style, p.Style);
        Assert.Equal(15, p.Runs.Single().GetEffectiveFormat().Size);
        Assert.Equal(Rgba8.FromHex("#112233"), copy.Theme.Colors[ThemeColor.Accent1]);
        Assert.Equal("Normal", copy.Styles.DefaultParagraphStyle!.Id);
    }

    [Fact]
    public void FormatSettersCheckValues()
    {
        RunFormat run = new();
        ParagraphFormat paragraph = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => run.Size = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => run.Size = double.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => run.Underline = (UnderlineStyle)99);
        Assert.Throws<ArgumentOutOfRangeException>(() => paragraph.OutlineLevel = 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TableFormat().RowBandSize = 0);
        paragraph.FirstLineIndent = -18;
        Assert.Equal(-18, paragraph.FirstLineIndent);
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentColor.FromTheme(ThemeColor.Accent1, tint: 2));
    }

    [Fact]
    public void ApplyAndClearFormatting()
    {
        RunFormat a = new() { Bold = true, Size = 12 };
        RunFormat b = new() { Size = 20, Italic = true };

        a.Apply(b);

        Assert.Equal((true, true, 20.0), (a.Bold, a.Italic, a.Size));
        a.ClearFormatting();
        Assert.True(a.IsEmpty);
        Assert.Throws<ArgumentException>(() => a.Apply(new ParagraphFormat()));
    }

    [Fact]
    public void ThemeColorsUseWordsLuminanceRules()
    {
        Theme theme = new Document().Theme;

        AssertClose(Rgba8.FromHex("#2F5496"), DocumentColor.FromTheme(ThemeColor.Accent1, shade: 1 - (0xBF / 255.0)).Resolve(theme, default));
        AssertClose(Rgba8.FromHex("#D9E2F3"), DocumentColor.FromTheme(ThemeColor.Accent1, tint: 1 - (0x33 / 255.0)).Resolve(theme, default));
        Assert.Equal("Accent1 shade 0.251", DocumentColor.FromTheme(ThemeColor.Accent1, shade: 1 - (0xBF / 255.0)).ToString());
        Assert.Equal("auto", DocumentColor.Auto.ToString());
    }

    /// <summary>Word's own tint and shade results, within one step per channel (Word rounds in its own HSL scale).</summary>
    private static void AssertClose(Rgba8 expected, Rgba8 actual)
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= 1 && Math.Abs(expected.G - actual.G) <= 1 && Math.Abs(expected.B - actual.B) <= 1,
            $"expected {expected.ToHex()}, got {actual.ToHex()}");
    }
}
