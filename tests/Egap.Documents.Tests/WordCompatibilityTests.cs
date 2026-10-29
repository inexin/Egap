using System.IO.Compression;
using System.Xml.Linq;

using Egap.Documents.Docx;
using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>Word's compatibility flags: the model, DOCX read and write, and each flag laid out on and off.</summary>
public class WordCompatibilityTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private const string Compat = """
        <w:compat><w:ulTrailSpace/><w:doNotExpandShiftReturn/><w:doNotUseIndentAsNumberingTabStop/><w:allowSpaceOfSameStyleInTable/>
        <w:splitPgBreakAndParaMark/>
        <w:compatSetting w:name="compatibilityMode" w:uri="http://schemas.microsoft.com/office/word" w:val="14"/>
        <w:compatSetting w:name="overrideTableStyleFontSizeAndJustification" w:uri="http://schemas.microsoft.com/office/word" w:val="0"/>
        <w:compatSetting w:name="doNotFlipMirrorIndents" w:uri="http://schemas.microsoft.com/office/word" w:val="1"/>
        </w:compat>
        """;

    [Fact]
    public void NewDocumentsHaveWord2013Settings()
    {
        WordCompatibility compat = new Document().Settings.WordCompatibility;

        Assert.True(compat.OverrideTableStyleFontSizeAndJustification);
        Assert.True(compat.EnableOpenTypeFeatures);
        Assert.False(compat.AllowSpaceOfSameStyleInTable);
        Assert.False(compat.TableIndentFromCellText);
        Assert.Equal("1", compat.OtherSettings["doNotFlipMirrorIndents"]);
        Assert.Equal("1", compat.OtherSettings["differentiateMultirowTableHeaders"]);
    }

    [Fact]
    public void FlagsFollowTheModeUnlessSet()
    {
        Document document = new();
        DocumentSettings settings = document.Settings;
        WordCompatibility compat = settings.WordCompatibility;
        List<string> changes = [];
        document.Changed += (_, e) => changes.Add(e.Property!);

        settings.CompatibilityMode = 14;
        Assert.True(compat.TableIndentFromCellText);

        compat.TableIndentFromCellText = false;
        settings.CompatibilityMode = 12;
        Assert.False(compat.TableIndentFromCellText);

        compat.ResetToModeDefaults();
        Assert.True(compat.TableIndentFromCellText);
        Assert.False(compat.EnableOpenTypeFeatures);
        Assert.Contains("Settings.WordCompatibility.TableIndentFromCellText", changes);
        Assert.Contains("Settings.WordCompatibility", changes);

        Document copy = (Document)document.Clone();
        Assert.False(copy.Settings.WordCompatibility.EnableOpenTypeFeatures);
        Assert.Equal(12, copy.Settings.CompatibilityMode);
    }

    [Fact]
    public void ReaderFillsFlagsAndKeepsTheRest()
    {
        WordCompatibility compat = Load(Compat).Settings.WordCompatibility;

        Assert.True(compat.AllowSpaceOfSameStyleInTable);
        Assert.True(compat.DoNotUseIndentAsNumberingTabStop);
        Assert.True(compat.SplitPageBreakAndParagraphMark);
        Assert.False(compat.DoNotBreakWrappedTables);
        Assert.False(compat.OverrideTableStyleFontSizeAndJustification);
        Assert.False(compat.EnableOpenTypeFeatures);
        Assert.True(compat.TableIndentFromCellText);
        Assert.Equal(new Dictionary<string, string> { ["ulTrailSpace"] = "1", ["doNotExpandShiftReturn"] = "1", ["doNotFlipMirrorIndents"] = "1" }, compat.OtherSettings);
    }

    [Fact]
    public void ReadFlagsGoBackUnchanged()
    {
        XElement compat = SettingsOf(Save(Load(Compat))).Element(W + "compat")!;

        Assert.Equal(XElement.Parse(Settings(Compat)).Element(W + "compat")!.ToString(SaveOptions.DisableFormatting), compat.ToString(SaveOptions.DisableFormatting));
    }

    [Fact]
    public void FlagsSetInCodeAreWrittenInSchemaOrder()
    {
        Document document = Load(Compat);
        WordCompatibility compat = document.Settings.WordCompatibility;
        compat.AllowSpaceOfSameStyleInTable = false;
        compat.DoNotBreakWrappedTables = true;
        compat.EnableOpenTypeFeatures = true;
        compat.OverrideTableStyleFontSizeAndJustification = true;

        XElement written = SettingsOf(Save(document)).Element(W + "compat")!;

        Assert.Equal(
            ["ulTrailSpace", "doNotExpandShiftReturn", "doNotBreakWrappedTables", "doNotUseIndentAsNumberingTabStop", "splitPgBreakAndParaMark",
                "compatSetting", "compatSetting", "compatSetting", "compatSetting"],
            written.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("1", Setting(written, "overrideTableStyleFontSizeAndJustification"));
        Assert.Equal("1", Setting(written, "enableOpenTypeFeatures"));

        WordCompatibility again = DocxFormat.Load(new MemoryStream(Save(document))).Settings.WordCompatibility;
        Assert.False(again.AllowSpaceOfSameStyleInTable);
        Assert.True(again.DoNotBreakWrappedTables);
        Assert.True(again.EnableOpenTypeFeatures);
    }

    [Fact]
    public void NewDocumentsWriteWord2013Settings()
    {
        XElement compat = SettingsOf(Save(new Document())).Element(W + "compat")!;

        Assert.Equal("15", Setting(compat, "compatibilityMode"));
        Assert.Equal("1", Setting(compat, "overrideTableStyleFontSizeAndJustification"));
        Assert.Equal("1", Setting(compat, "enableOpenTypeFeatures"));
        Assert.Equal("1", Setting(compat, "doNotFlipMirrorIndents"));
        Assert.Equal("1", Setting(compat, "differentiateMultirowTableHeaders"));
        Assert.Equal(5, compat.Elements().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllowSpaceOfSameStyleInTable(bool on)
    {
        Document document = new();
        ParagraphStyle style = document.Styles.AddParagraphStyle("Tight");
        style.ParagraphFormat.ContextualSpacing = true;
        style.ParagraphFormat.SpaceAfter = 30;
        Table table = Body(document).AddTable(1, 1);
        Cell cell = table.Rows[0].Cells[0];
        Paragraph first = cell.Paragraphs.Single();
        first.AddRun("a");
        Paragraph second = cell.AddParagraph("b");
        first.Style = second.Style = style;
        document.Settings.WordCompatibility.AllowSpaceOfSameStyleInTable = on;

        DocumentLayout layout = RoundTrip(document).Layout(Isolated);

        Paragraph[] read = [.. Cells(layout).Single().Paragraphs];
        double gap = Top(layout, read[1]) - Bottom(layout, read[0]);
        Assert.Equal(on ? 30 : 0, gap, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoNotUseIndentAsNumberingTabStop(bool on)
    {
        Document document = new();
        Paragraph paragraph = Body(document).AddParagraph();
        paragraph.SetList(document.Lists.AddNumbered());
        paragraph.Format.LeftIndent = 50;
        paragraph.Format.FirstLineIndent = -30;
        Run text = paragraph.AddRun("text");
        document.Settings.WordCompatibility.DoNotUseIndentAsNumberingTabStop = on;

        Document read = RoundTrip(document);
        DocumentLayout layout = read.Layout(Isolated);

        const double margin = 72;
        double x = LayoutInteraction.TextRects(layout, read.Sections[0].Body.Paragraphs.First().Runs.Single()).First().Bounds.X - margin;
        if (on)
        {
            Assert.True(x > 51, $"the label's tab went to the hanging indent ({x})");
        }
        else
        {
            Assert.Equal(50, x, 1);
        }
        _ = text;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SplitPageBreakAndParagraphMark(bool on)
    {
        Document document = new();
        Body body = Body(document);
        Paragraph first = body.AddParagraph("a");
        first.AddBreak(BreakKind.Page);
        body.AddParagraph("b");
        document.Settings.WordCompatibility.SplitPageBreakAndParagraphMark = on;

        Document read = RoundTrip(document);
        DocumentLayout layout = read.Layout(Isolated);

        Paragraph next = read.Sections[0].Body.Paragraphs.ElementAt(1);
        (PageLayout page, LayoutBox box) = layout.FindBoxes(next).First(b => b.Box is ParagraphBox);
        Assert.Same(layout.Pages[1], page);
        double lineHeight = ((ParagraphBox)layout.FindBoxes(read.Sections[0].Body.Paragraphs.First())[0].Box).Lines[0].Bounds.Height;
        const double top = 72;
        Assert.Equal(on ? top + lineHeight + first.GetEffectiveFormat().SpaceAfter : top, box.Bounds.Y, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverrideTableStyleFontSizeAndJustification(bool on)
    {
        Document document = new();
        ParagraphStyle normal = document.Styles.DefaultParagraphStyle!;
        normal.RunFormat.Size = 11;
        normal.ParagraphFormat.Alignment = ParagraphAlignment.Left;
        TableStyle style = document.Styles.AddTableStyle("Small");
        style.RunFormat.Size = 8;
        style.ParagraphFormat.Alignment = ParagraphAlignment.Center;
        Table table = Body(document).AddTable(1, 1);
        table.Style = style;
        table.Rows[0].Cells[0].Paragraphs.Single().AddRun("x");
        Paragraph outside = document.Sections[0].Body.AddParagraph("y");
        document.Settings.WordCompatibility.OverrideTableStyleFontSizeAndJustification = on;

        Document read = RoundTrip(document);

        Paragraph inCell = read.Sections[0].Body.Tables.Single().Rows[0].Cells[0].Paragraphs.Single();
        Assert.Equal(on ? 11 : 8, inCell.Runs.Single().GetEffectiveFormat().Size);
        Assert.Equal(on ? ParagraphAlignment.Left : ParagraphAlignment.Center, inCell.GetEffectiveFormat().Alignment);
        Assert.Equal(11, read.Sections[0].Body.Paragraphs.Last().Runs.Single().GetEffectiveFormat().Size);
        _ = outside;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnableOpenTypeFeatures(bool on)
    {
        Document document = new();
        Run run = Body(document).AddParagraph().AddRun("ffi");
        run.Format.Features = FontFeatures.Parse("liga=1");
        document.Settings.WordCompatibility.EnableOpenTypeFeatures = on;

        Document read = RoundTrip(document);
        ParagraphContent content = new ParagraphBridge(new FontProvider(read, new FontSettings { UseSystemFonts = false }), read).Build(read.Sections[0].Body.Paragraphs.First());

        bool asked = content.Text.RichText.Spans[^1].Style.Features!.Any(f => f.ToString().StartsWith("liga=1", StringComparison.Ordinal));
        Assert.Equal(on, asked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TableIndentFromCellText(bool on)
    {
        Document document = new();
        Table table = Body(document).AddTable(1, 1);
        table.Rows[0].Cells[0].Paragraphs.Single().AddRun("x");
        document.Settings.CompatibilityMode = on ? 14 : 15;

        DocumentLayout layout = RoundTrip(document).Layout(Isolated);

        const double margin = 72;
        Assert.Equal(on ? -5.4 : 0, layout.FindBoxes(layout.Document.Sections[0].Body.Tables.Single()).Single().Box.Bounds.X - margin, 1);
    }

    private static Body Body(Document document)
    {
        Section section = document.AddSection();
        section.PageSetup.MarginLeft = 72;
        section.PageSetup.MarginTop = 72;
        return section.Body;
    }

    private static IEnumerable<Cell> Cells(DocumentLayout layout)
    {
        return layout.Document.Sections[0].Body.Tables.Single().Rows.SelectMany(r => r.Cells);
    }

    private static double Top(DocumentLayout layout, Paragraph paragraph) => layout.FindBoxes(paragraph).First(b => b.Box is ParagraphBox).Box.Bounds.Y;

    private static double Bottom(DocumentLayout layout, Paragraph paragraph) => layout.FindBoxes(paragraph).First(b => b.Box is ParagraphBox).Box.Bounds.Bottom;

    private static Document RoundTrip(Document document) => DocxFormat.Load(new MemoryStream(Save(document)));

    private static byte[] Save(Document document)
    {
        using MemoryStream stream = new();
        DocxFormat.Save(document, stream);
        return stream.ToArray();
    }

    private static XElement SettingsOf(byte[] docx)
    {
        using ZipArchive zip = new(new MemoryStream(docx));
        using Stream part = zip.GetEntry("word/settings.xml")!.Open();
        return XElement.Load(part);
    }

    private static string? Setting(XElement compat, string name)
    {
        return (string?)compat.Elements(W + "compatSetting").SingleOrDefault(s => (string?)s.Attribute(W + "name") == name)?.Attribute(W + "val");
    }

    private static string Settings(string compat)
    {
        return "<w:settings xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" + compat.ReplaceLineEndings("") + "</w:settings>";
    }

    /// <summary>A DOCX whose settings part holds <paramref name="compat"/>, loaded.</summary>
    private static Document Load(string compat)
    {
        using MemoryStream source = new(Save(new Document()));
        using MemoryStream patched = new();
        source.CopyTo(patched);
        using (ZipArchive zip = new(patched, ZipArchiveMode.Update, leaveOpen: true))
        {
            zip.GetEntry("word/settings.xml")!.Delete();
            using StreamWriter writer = new(zip.CreateEntry("word/settings.xml").Open());
            writer.Write(Settings(compat));
        }

        patched.Position = 0;
        return DocxFormat.Load(patched);
    }
}
