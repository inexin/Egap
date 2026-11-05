using System.Text;
using System.Xml.Linq;

namespace Egap.Spreadsheets.Tests;

/// <summary>Hyperlinks, data validation, conditional formatting, tables, notes and pictures: made, read, edited and kept.</summary>
public sealed class SheetObjectTests
{
    private static readonly XNamespace Ns = XlsxSeed.Main;
    private static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F'];

    [Fact]
    public void ObjectsMadeInCodeComeBack()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet["A1"].Value = "Item";
        sheet["B1"].Value = 1;
        sheet["A2"].Value = "Pear";
        sheet["B2"].Value = 2;
        sheet["A3"].Value = "Plum";
        sheet["B3"].Value = 3;

        Hyperlink web = sheet.Hyperlinks.Add("D1", "https://example.com/a?b=c");
        web.Display = "Example";
        web.ToolTip = "Opens the site";
        sheet.Hyperlinks.AddInternal(RangeAddress.Parse("D2"), "'Sheet1'!A1");
        DataValidation list = sheet.DataValidations.AddList(RangeAddress.Parse("E1:E9"), ["Yes", "No"]);
        list.Prompt = "Pick one";
        sheet.DataValidations.Add(RangeAddress.Parse("F1:F9"), DataValidationType.WholeNumber, "1", "10").ShowDropDown = false;
        CellStyle red = CellStyle.Default with { Font = CellFont.Default with { Bold = true, Color = CellColor.FromRgb(200, 0, 0) }, Fill = CellFill.Solid(CellColor.FromRgb(255, 230, 230)) };
        sheet.ConditionalFormats.Add("B1:B3", ConditionalFormatRule.CellIs(ComparisonOperator.GreaterThan, "2", red), ConditionalFormatRule.DataBar(CellColor.FromRgb(0, 0, 255)));
        sheet.ConditionalFormats.Add("A1:A3", ConditionalFormatRule.Expression("LEN(A1)>3", red));
        SheetTable table = sheet.Tables.Add("A1:B4", "Fruit");
        table.ShowTotalsRow = true;
        table.Columns[1].TotalsRowFunction = TableTotalsFunction.Sum;
        table.Columns[0].TotalsRowLabel = "Total";
        sheet.Comments.Add("B2", "Two\nlines", "Ann").Visible = true;
        sheet.Comments.Add("A3", "Plain");
        SheetDrawing picture = sheet.Drawings.AddPicture(Png, CellAddress.Parse("H2"), 64, 32);
        picture.Description = "Logo";
        sheet.Drawings.AddPicture(Jpeg, RangeAddress.Parse("H5:J8"));

        (Workbook loaded, MemoryStream package) = XlsxSeed.RoundTrip(book);
        Worksheet back = loaded.Worksheets[0];

        Assert.Equal(2, back.Hyperlinks.Count);
        Assert.Equal(("https://example.com/a?b=c", "Example", "Opens the site"), (back.Hyperlinks[0].Address, back.Hyperlinks[0].Display, back.Hyperlinks[0].ToolTip));
        Assert.Equal("'Sheet1'!A1", back.Hyperlinks.Find(CellAddress.Parse("D2"))!.Location);
        Assert.Equal(DataValidationType.List, back.DataValidations[0].Type);
        Assert.Equal("\"Yes,No\"", back.DataValidations[0].Formula1);
        Assert.Equal("Pick one", back.DataValidations[0].Prompt);
        Assert.True(back.DataValidations[0].ShowDropDown);
        Assert.Equal(("1", "10", false), (back.DataValidations[1].Formula1, back.DataValidations[1].Formula2, back.DataValidations[1].ShowDropDown));
        Assert.Equal(2, back.ConditionalFormats.Count);
        ConditionalFormatRule cellIs = back.ConditionalFormats[0].Rules[0];
        Assert.Equal((ConditionalFormatType.CellIs, ComparisonOperator.GreaterThan, "2"), (cellIs.Type, cellIs.Operator, cellIs.Formulas.Single()));
        Assert.Equal(red, cellIs.Style);
        Assert.Equal(ConditionalFormatType.DataBar, back.ConditionalFormats[0].Rules[1].Type);
        Assert.Equal([1, 2, 3], back.ConditionalFormats.SelectMany(f => f.Rules).Select(r => r.Priority).Order());
        Assert.Equal(red, back.ConditionalFormats[1].Rules[0].Style);

        SheetTable fruit = back.Tables["fruit"];
        Assert.Equal(RangeAddress.Parse("A1:B4"), fruit.Range);
        Assert.True(fruit.ShowTotalsRow);
        Assert.Equal(["Item", "1"], fruit.Columns.Select(c => c.Name));
        Assert.Equal("1", back["B1"].Value);
        Assert.Equal(TableTotalsFunction.Sum, fruit.Columns[1].TotalsRowFunction);
        Assert.Equal("Total", fruit.Columns[0].TotalsRowLabel);

        Assert.Equal(("Two\nlines", "Ann", true), (back.Comments[CellAddress.Parse("B2")]!.Text, back.Comments[CellAddress.Parse("B2")]!.Author, back.Comments[CellAddress.Parse("B2")]!.Visible));
        Assert.False(back.Comments[CellAddress.Parse("A3")]!.Visible);

        Assert.Equal(2, back.Drawings.Count);
        SheetDrawing logo = back.Drawings[0];
        Assert.Equal((SheetDrawingKind.Picture, DrawingAnchorKind.OneCell, "Logo"), (logo.Kind, logo.AnchorKind, logo.Description));
        Assert.Equal((64 * 9525L, 32 * 9525L), (logo.Width, logo.Height));
        Assert.Equal(Png, logo.ImageData);
        Assert.Equal("image/jpeg", back.Drawings[1].ImageContentType);
        Assert.Equal(new DrawingPosition(CellAddress.Parse("K9")), back.Drawings[1].To);

        Dictionary<string, byte[]> parts = XlsxSeed.Parts(package);
        string types = Encoding.UTF8.GetString(parts["[Content_Types].xml"]);
        Assert.Contains("spreadsheetml.table+xml", types, StringComparison.Ordinal);
        Assert.Contains("spreadsheetml.comments+xml", types, StringComparison.Ordinal);
        Assert.Contains("drawing+xml", types, StringComparison.Ordinal);
        Assert.Contains("xl/media/image1.png", parts.Keys);
        XElement worksheet = XlsxSeed.Xml(parts, "xl/worksheets/sheet1.xml").Root!;
        Assert.Equal(
            ["dimension", "sheetViews", "sheetFormatPr", "sheetData", "conditionalFormatting", "conditionalFormatting", "dataValidations",
                "hyperlinks", "pageMargins", "drawing", "legacyDrawing", "tableParts"],
            worksheet.Elements().Select(e => e.Name.LocalName).Where(n => n != "cols"));
        XElement tableXml = XlsxSeed.Xml(parts, "xl/tables/table1.xml").Root!;
        Assert.Equal(["autoFilter", "tableColumns", "tableStyleInfo"], tableXml.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("A1:B3", (string?)tableXml.Element(Ns + "autoFilter")!.Attribute("ref"));
        XElement styles = XlsxSeed.Xml(parts, "xl/styles.xml").Root!;
        Assert.Single(styles.Element(Ns + "dxfs")!.Elements());
    }

    [Fact]
    public void AnExcelFilesObjectsLoad()
    {
        Workbook book = XlsxSeed.LoadFeatures();
        Worksheet data = book.Worksheets["Data"];

        Hyperlink link = data.Hyperlinks.Single();
        Assert.Equal((RangeAddress.Parse("A12"), "https://example.com/", "Example"), (link.Range, link.Address, link.Display));
        DataValidation rule = data.DataValidations.Single();
        Assert.Equal((DataValidationType.WholeNumber, ComparisonOperator.Between, "0", "100"), (rule.Type, rule.Operator, rule.Formula1, rule.Formula2));
        Assert.Equal(ConditionalFormatType.DataBar, data.ConditionalFormats.Single().Rules.Single().Type);
        CellComment note = data.Comments.Single();
        Assert.Equal((CellAddress.Parse("C2"), "A note", "Tester"), (note.Cell, note.Text, note.Author));
        SheetTable table = book.Worksheets["Table"].Tables.Single();
        Assert.Equal(("Table1", "TableStyleMedium2"), (table.Name, table.StyleName));
        Assert.Equal(["Name", "Score"], table.Columns.Select(c => c.Name));
        Assert.Same(table, book.Worksheets["Table"].Tables.Find(CellAddress.Parse("B2")));
    }

    [Fact]
    public void UntouchedObjectPartsKeepTheirBytes()
    {
        Dictionary<string, string> seed = WithPictureAndChart();
        Workbook book = Workbook.Load(XlsxSeed.Package(seed));
        book.Worksheets["Data"]["A1"].Value = "Changed";

        (_, MemoryStream package) = XlsxSeed.RoundTrip(book);
        Dictionary<string, byte[]> parts = XlsxSeed.Parts(package);

        foreach (string name in (string[])["xl/tables/table1.xml", "xl/comments1.xml", "xl/drawings/drawing1.xml", "xl/drawings/vmlDrawing1.vml", "xl/charts/chart1.xml", "xl/media/image1.png"])
        {
            Assert.Equal(Encoding.UTF8.GetBytes(Original(seed, name)), parts[name]);
        }
    }

    [Fact]
    public void EditsRewriteOnlyTheirParts()
    {
        Dictionary<string, string> seed = WithPictureAndChart();
        Workbook book = Workbook.Load(XlsxSeed.Package(seed));
        Worksheet data = book.Worksheets["Data"];
        SheetTable table = book.Worksheets["Table"].Tables[0];
        table.Name = "Scores";
        table.StyleName = "TableStyleLight9";
        SheetDrawing picture = data.Drawings.Single(d => d.Kind == SheetDrawingKind.Picture);
        picture.ImageData = Jpeg;
        picture.Name = "Replaced";
        data.Comments.Clear();

        (Workbook loaded, MemoryStream package) = XlsxSeed.RoundTrip(book);
        Dictionary<string, byte[]> parts = XlsxSeed.Parts(package);

        Assert.Equal("Scores", loaded.Worksheets["Table"].Tables[0].Name);
        Assert.Equal("TableStyleLight9", loaded.Worksheets["Table"].Tables[0].StyleName);
        Assert.Equal("1", (string?)XlsxSeed.Xml(parts, "xl/tables/table1.xml").Root!.Attribute("id"));

        Worksheet back = loaded.Worksheets["Data"];
        Assert.Empty(back.Comments);
        Assert.False(parts.ContainsKey("xl/comments1.xml"));
        Assert.False(parts.ContainsKey("xl/drawings/vmlDrawing1.vml"));
        Assert.Null(XlsxSeed.Xml(parts, "xl/worksheets/sheet1.xml").Root!.Element(Ns + "legacyDrawing"));

        Assert.Equal([SheetDrawingKind.Chart, SheetDrawingKind.Picture], back.Drawings.Select(d => d.Kind));
        Assert.Equal(Jpeg, back.Drawings[1].ImageData);
        Assert.Equal("Replaced", back.Drawings[1].Name);
        Assert.Equal(Encoding.UTF8.GetBytes(Original(seed, "xl/charts/chart1.xml")), parts["xl/charts/chart1.xml"]);
        Assert.False(parts.ContainsKey("xl/media/image1.png"));
        XElement anchor = XlsxSeed.Xml(parts, "xl/drawings/drawing1.xml").Root!.Elements().Last();
        Assert.NotNull(anchor.Descendants(A + "extLst").SingleOrDefault());
    }

    [Fact]
    public void RemovingEveryObjectDropsTheirParts()
    {
        Workbook book = Workbook.Load(XlsxSeed.Package(WithPictureAndChart()));
        Worksheet data = book.Worksheets["Data"];
        foreach (SheetDrawing drawing in data.Drawings.ToArray())
        {
            data.Drawings.Remove(drawing);
        }

        data.Hyperlinks.Clear();
        book.Worksheets["Table"].Tables.Remove(book.Worksheets["Table"].Tables[0]);

        (_, MemoryStream package) = XlsxSeed.RoundTrip(book);
        Dictionary<string, byte[]> parts = XlsxSeed.Parts(package);

        foreach (string gone in (string[])["xl/drawings/drawing1.xml", "xl/charts/chart1.xml", "xl/media/image1.png", "xl/tables/table1.xml"])
        {
            Assert.False(parts.ContainsKey(gone), gone);
        }

        string rels = Encoding.UTF8.GetString(parts["xl/worksheets/_rels/sheet1.xml.rels"]);
        Assert.DoesNotContain("example.com", rels, StringComparison.Ordinal);
        Assert.DoesNotContain("drawing1", rels, StringComparison.Ordinal);
        Assert.Null(XlsxSeed.Xml(parts, "xl/worksheets/sheet3.xml").Root!.Element(Ns + "tableParts"));
    }

    [Fact]
    public void AnExistingDifferentialFormatKeepsItsIndex()
    {
        Dictionary<string, string> seed = XlsxSeed.Features();
        seed["xl/styles.xml"] = seed["xl/styles.xml"].Replace("<dxfs count=\"0\"/>",
            "<dxfs count=\"1\"><dxf><font><b/><color rgb=\"FF9C0006\"/></font><fill><patternFill><bgColor rgb=\"FFFFC7CE\"/></patternFill></fill></dxf></dxfs>", StringComparison.Ordinal);
        seed["xl/worksheets/sheet1.xml"] = seed["xl/worksheets/sheet1.xml"].Replace("<cfRule type=\"dataBar\" priority=\"1\">",
            "<cfRule type=\"expression\" dxfId=\"0\" priority=\"2\"><formula>$B2&gt;2</formula></cfRule><cfRule type=\"dataBar\" priority=\"1\">", StringComparison.Ordinal);
        Workbook book = Workbook.Load(XlsxSeed.Package(seed));
        Worksheet data = book.Worksheets["Data"];
        ConditionalFormatRule read = data.ConditionalFormats[0].Rules[0];
        Assert.True(read.Style!.Font.Bold);
        Assert.Equal(CellColor.FromRgb(0xFF, 0xC7, 0xCE), read.Style.Fill.Foreground);
        data.ConditionalFormats.Add("C2:C6", ConditionalFormatRule.Expression("C2>5", CellStyle.Default with { Font = CellFont.Default with { Italic = true } }));

        (Workbook loaded, MemoryStream package) = XlsxSeed.RoundTrip(book);
        Dictionary<string, byte[]> parts = XlsxSeed.Parts(package);

        XElement sheet = XlsxSeed.Xml(parts, "xl/worksheets/sheet1.xml").Root!;
        Assert.Equal(["0", null, "1"], sheet.Elements(Ns + "conditionalFormatting").Elements().Select(r => (string?)r.Attribute("dxfId")));
        Assert.Equal(["2", "1", "3"], sheet.Elements(Ns + "conditionalFormatting").Elements().Select(r => (string?)r.Attribute("priority")));
        Assert.Equal(2, XlsxSeed.Xml(parts, "xl/styles.xml").Root!.Element(Ns + "dxfs")!.Elements().Count());
        Assert.True(loaded.Worksheets["Data"].ConditionalFormats[1].Rules[0].Style!.Font.Italic);
    }

    [Fact]
    public void AHeaderCellTheSaveWritesIsANewString()
    {
        Workbook book = XlsxSeed.LoadFeatures();
        Worksheet sheet = book.Worksheets["Table"];
        sheet["B1"].Value = 7;
        sheet.Tables[0].Columns[1].Name = "Points";

        (Workbook loaded, _) = XlsxSeed.RoundTrip(book);

        Assert.Equal("Points", loaded.Worksheets["Table"]["B1"].Value);
        Assert.Equal(["Name", "Points"], loaded.Worksheets["Table"].Tables[0].Columns.Select(c => c.Name));
    }

    [Fact]
    public void TableNamesFollowExcelsRules()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        book.Names.Add("Taken", "=1");
        SheetTable first = sheet.Tables.Add("A1:B3");
        Assert.Equal("Table1", first.Name);
        Assert.Equal(["Column1", "Column2"], first.Columns.Select(c => c.Name));
        Assert.Equal("Column1", sheet["A1"].Value);

        Assert.Throws<ArgumentException>(() => sheet.Tables.Add("D1:E3", "table1"));
        Assert.Throws<ArgumentException>(() => sheet.Tables.Add("D1:E3", "Taken"));
        Assert.Throws<ArgumentException>(() => sheet.Tables.Add("D1:E3", "A1"));
        Assert.Throws<ArgumentException>(() => sheet.Tables.Add("D1:E3", "1st"));
        Assert.Throws<ArgumentException>(() => sheet.Tables.Add("B2:C4", "Overlap"));
        Assert.Throws<ArgumentException>(() => sheet.Tables.Add("D1:E1", "OneRow"));
        Assert.Throws<ArgumentException>(() => first.Name = "R");

        sheet["G1"].Value = "Same";
        sheet["H1"].Value = "same";
        SheetTable second = sheet.Tables.Add("G1:H2", "Second");
        Assert.Equal(["Same", "same2"], second.Columns.Select(c => c.Name));
        Assert.Equal("same2", sheet["H1"].Value);
    }

    [Fact]
    public void PicturesNeedImagesExcelShows()
    {
        Worksheet sheet = Workbook.Create().Worksheets[0];
        Assert.Throws<ArgumentException>(() => sheet.Drawings.AddPicture([1, 2, 3], RangeAddress.Parse("A1:B2")));
        Assert.Throws<ArgumentOutOfRangeException>(() => sheet.Drawings.AddPicture(Png, CellAddress.Parse("A1"), 0, 5));
        Assert.Empty(sheet.Drawings);
        Assert.Throws<ArgumentException>(() => sheet.Comments.Add("A1", "x") is { } && sheet.Comments.Add("A1", "y") is { });
    }

    private static string Original(Dictionary<string, string> seed, string name) => seed[name].Trim();

    /// <summary>The features workbook with a drawing that holds a chart and a picture, and a VML drawing with a note shape.</summary>
    private static Dictionary<string, string> WithPictureAndChart()
    {
        const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        Dictionary<string, string> seed = XlsxSeed.Features();
        seed["[Content_Types].xml"] = seed["[Content_Types].xml"].Replace("<Default Extension=\"xml\"", "<Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"xml\"", StringComparison.Ordinal)
            .Replace("</Types>", "<Override PartName=\"/xl/charts/chart1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawingml.chart+xml\"/></Types>", StringComparison.Ordinal);
        seed["xl/drawings/drawing1.xml"] = """
            <xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"><xdr:twoCellAnchor editAs="oneCell"><xdr:from><xdr:col>1</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>1</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:to><xdr:col>6</xdr:col><xdr:colOff>0</xdr:colOff><xdr:row>12</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to><xdr:graphicFrame macro=""><xdr:nvGraphicFramePr><xdr:cNvPr id="2" name="Chart 1"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr><xdr:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/></xdr:xfrm><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/chart"><c:chart xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:id="rId1"/></a:graphicData></a:graphic></xdr:graphicFrame><xdr:clientData/></xdr:twoCellAnchor><xdr:oneCellAnchor><xdr:from><xdr:col>8</xdr:col><xdr:colOff>9525</xdr:colOff><xdr:row>2</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from><xdr:ext cx="952500" cy="476250"/><xdr:pic><xdr:nvPicPr><xdr:cNvPr id="3" name="Picture 2" descr="Logo"><a:extLst><a:ext uri="{FF2B5EF4-FFF2-40B4-BE49-F238E27FC236}"/></a:extLst></xdr:cNvPr><xdr:cNvPicPr><a:picLocks noChangeAspect="1"/></xdr:cNvPicPr></xdr:nvPicPr><xdr:blipFill><a:blip xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" r:embed="rId2"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill><xdr:spPr><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic><xdr:clientData/></xdr:oneCellAnchor></xdr:wsDr>
            """;
        seed["xl/drawings/_rels/drawing1.xml.rels"] = $"""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{Rel}chart" Target="../charts/chart1.xml"/><Relationship Id="rId2" Type="{Rel}image" Target="../media/image1.png"/></Relationships>""";
        seed["xl/charts/chart1.xml"] = """<c:chartSpace xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart"><c:chart/></c:chartSpace>""";
        seed["xl/media/image1.png"] = "\u0089PNG fake";
        seed["xl/drawings/vmlDrawing1.vml"] = """<xml xmlns:v="urn:schemas-microsoft-com:vml" xmlns:x="urn:schemas-microsoft-com:office:excel"><v:shape id="_x0000_s1025"><x:ClientData ObjectType="Note"><x:Row>1</x:Row><x:Column>2</x:Column></x:ClientData></v:shape></xml>""";
        return seed;
    }
}
