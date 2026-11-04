using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Small workbook packages shaped as Excel writes them (ECMA-376 Part 1, chapter 18): parts, content types and
/// relationships as Excel lays them out, with the extensions Excel adds, for the round-trip tests.
/// </summary>
internal static class XlsxSeed
{
    public const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Ns = "xmlns=\"" + Main + "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
    private const string Ext = " xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" mc:Ignorable=\"x14ac xr xr2 xr3\""
        + " xmlns:x14ac=\"http://schemas.microsoft.com/office/spreadsheetml/2009/9/ac\" xmlns:xr=\"http://schemas.microsoft.com/office/spreadsheetml/2014/revision\""
        + " xmlns:xr2=\"http://schemas.microsoft.com/office/spreadsheetml/2015/revision2\" xmlns:xr3=\"http://schemas.microsoft.com/office/spreadsheetml/2016/revision3\"";

    private const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string Ml = "application/vnd.openxmlformats-officedocument.spreadsheetml.";

    /// <summary>A workbook as Excel saves one with three sheets, one hidden, and a chart sheet, styles, rich text, names, a comment, a drawing and a table.</summary>
    public static Dictionary<string, string> Features() => new()
    {
        ["[Content_Types].xml"] = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="bin" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.printerSettings"/><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Default Extension="vml" ContentType="application/vnd.openxmlformats-officedocument.vmlDrawing"/>
            <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
            <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            <Override PartName="/xl/worksheets/sheet3.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            <Override PartName="/xl/chartsheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.chartsheet+xml"/>
            <Override PartName="/xl/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/>
            <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
            <Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>
            <Override PartName="/xl/calcChain.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.calcChain+xml"/>
            <Override PartName="/xl/comments1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.comments+xml"/>
            <Override PartName="/xl/tables/table1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.table+xml"/>
            <Override PartName="/xl/drawings/drawing1.xml" ContentType="application/vnd.openxmlformats-officedocument.drawing+xml"/>
            <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
            <Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/></Types>
            """,
        ["_rels/.rels"] = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """,
        ["docProps/core.xml"] = """<cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:creator>Tester</dc:creator></cp:coreProperties>""",
        ["docProps/app.xml"] = """<Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"><Application>Microsoft Excel</Application></Properties>""",
        ["xl/_rels/workbook.xml.rels"] = $"""
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId3" Type="{Rel}worksheet" Target="worksheets/sheet3.xml"/><Relationship Id="rId7" Type="{Rel}sharedStrings" Target="sharedStrings.xml"/><Relationship Id="rId2" Type="{Rel}worksheet" Target="worksheets/sheet2.xml"/><Relationship Id="rId1" Type="{Rel}worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId6" Type="{Rel}styles" Target="styles.xml"/><Relationship Id="rId5" Type="{Rel}theme" Target="theme/theme1.xml"/><Relationship Id="rId4" Type="{Rel}chartsheet" Target="chartsheets/sheet1.xml"/><Relationship Id="rId8" Type="{Rel}calcChain" Target="calcChain.xml"/></Relationships>
            """,
        ["xl/workbook.xml"] = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook {Ns}{Ext}><fileVersion appName="xl" lastEdited="7" lowestEdited="7" rupBuild="27831"/><workbookPr defaultThemeVersion="202300"/><mc:AlternateContent xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"><mc:Choice Requires="x15"><x15ac:absPath xmlns:x15ac="http://schemas.microsoft.com/office/spreadsheetml/2010/11/ac" url="C:\Temp\"/></mc:Choice></mc:AlternateContent><bookViews><workbookView xWindow="-120" yWindow="-120" windowWidth="29040" windowHeight="15720" activeTab="3"/></bookViews><sheets><sheet name="Data" sheetId="1" r:id="rId1"/><sheet name="Hidden" sheetId="2" state="hidden" r:id="rId2"/><sheet name="Chart1" sheetId="4" r:id="rId4"/><sheet name="Table" sheetId="3" r:id="rId3"/></sheets><definedNames><definedName name="_xlnm.Print_Area" localSheetId="0">Data!$A$1:$E$7</definedName><definedName name="_xlnm.Print_Titles" localSheetId="0">Data!$1:$1</definedName><definedName name="ChartLocal" localSheetId="2" hidden="1">Data!$B$2</definedName><definedName name="LocalRange" localSheetId="0">Data!$A$2:$A$6</definedName><definedName name="TaxRate" comment="Rate">0.25</definedName></definedNames><calcPr calcId="191029"/><extLst><ext uri="{"{"}140A7094-0E35-4892-8432-C4D2E57EDEB5{"}"}" xmlns:x15="http://schemas.microsoft.com/office/spreadsheetml/2010/11/main"><x15:workbookPr chartTrackingRefBase="1"/></ext></extLst></workbook>
            """,
        ["xl/theme/theme1.xml"] = """<a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Office Theme"><a:themeElements/></a:theme>""",
        ["xl/calcChain.xml"] = $"""<calcChain xmlns="{Main}"><c r="D2" i="1"/><c r="D3"/></calcChain>""",
        ["xl/sharedStrings.xml"] = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <sst xmlns="{Main}" count="12" uniqueCount="11"><si><t>Item</t></si><si><t>Price</t></si><si><t>Qty</t></si><si><t>Apple</t></si><si><t>Pear</t></si><si><t xml:space="preserve">Line one
            Line two</t></si><si><t xml:space="preserve"> padded </t></si><si><r><rPr><b/><sz val="11"/><color rgb="FFFF0000"/><rFont val="Calibri"/><family val="2"/><scheme val="minor"/></rPr><t>Bold</t></r><r><rPr><sz val="11"/><color theme="1"/><rFont val="Calibri"/><family val="2"/><scheme val="minor"/></rPr><t xml:space="preserve"> and plain</t></r></si><si><t>secret</t></si><si><t>Carriage_x000D_return and _x005F_x0041_</t></si><si><t>Unused</t></si></sst>
            """,
        ["xl/styles.xml"] = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet {Ns}{Ext}><numFmts count="1"><numFmt numFmtId="164" formatCode="#,##0.00\ &quot;kr&quot;"/></numFmts><fonts count="3" x14ac:knownFonts="1"><font><sz val="11"/><color theme="1"/><name val="Calibri"/><family val="2"/><scheme val="minor"/></font><font><b/><sz val="11"/><color theme="1"/><name val="Calibri"/><family val="2"/><scheme val="minor"/></font><font><u/><sz val="11"/><color theme="10"/><name val="Calibri"/><family val="2"/><scheme val="minor"/></font></fonts><fills count="4"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFFFE0C0"/><bgColor indexed="64"/></patternFill></fill><fill><gradientFill degree="90"><stop position="0"><color theme="0"/></stop><stop position="1"><color theme="4"/></stop></gradientFill></fill></fills><borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border><border><left/><right/><top/><bottom style="thin"><color indexed="64"/></bottom><diagonal/></border></borders><cellStyleXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/><xf numFmtId="0" fontId="2" fillId="0" borderId="0" applyNumberFormat="0" applyFill="0" applyBorder="0" applyAlignment="0" applyProtection="0"/></cellStyleXfs><cellXfs count="8"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="14" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment wrapText="1"/></xf><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment horizontal="center"/></xf><xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="1"/><xf numFmtId="0" fontId="0" fillId="3" borderId="0" xfId="0" applyFill="1"/></cellXfs><cellStyles count="2"><cellStyle name="Hyperlink" xfId="1" builtinId="8"/><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles><dxfs count="0"/><tableStyles count="0" defaultTableStyle="TableStyleMedium2" defaultPivotStyle="PivotStyleLight16"/><extLst><ext uri="{"{"}EB79DEF2-80B8-43e5-95BD-54CBDDF9020C{"}"}" xmlns:x14="http://schemas.microsoft.com/office/spreadsheetml/2009/9/main"><x14:slicerStyles defaultSlicerStyle="SlicerStyleLight1"/></ext></extLst></styleSheet>
            """,
        ["xl/worksheets/sheet1.xml"] = $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet {Ns}{Ext} xr:uid="{"{"}00000000-0001-0000-0000-000000000000{"}"}"><sheetPr><tabColor rgb="FF00B050"/><outlinePr summaryBelow="0"/><pageSetUpPr fitToPage="1"/></sheetPr><dimension ref="A1:F12"/><sheetViews><sheetView tabSelected="1" zoomScale="120" workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/><selection pane="bottomLeft" activeCell="B4" sqref="B4"/></sheetView></sheetViews><sheetFormatPr defaultRowHeight="15" outlineLevelRow="2" x14ac:dyDescent="0.25"/><cols><col min="1" max="1" width="18" customWidth="1"/><col min="6" max="6" width="9.140625" hidden="1" customWidth="1"/><col min="7" max="16384" width="9.140625" style="5"/></cols><sheetData><row r="1" spans="1:6" x14ac:dyDescent="0.25"><c r="A1" s="1" t="s"><v>0</v></c><c r="B1" s="1" t="s"><v>1</v></c><c r="C1" s="1" t="s"><v>2</v></c><c r="D1" s="1" t="inlineStr"><is><t>Total</t></is></c><c r="E1" s="1" t="str"><v>When</v></c></row><row r="2" spans="1:6" x14ac:dyDescent="0.25"><c r="A2" t="s"><v>3</v></c><c r="B2" s="2"><v>1.25</v></c><c r="C2"><v>10</v></c><c r="D2"><f t="shared" ref="D2:D3" si="0">B2*C2</f><v>12.5</v></c><c r="E2" s="3"><v>45000</v></c><c r="F2" t="b"><v>1</v></c></row><row r="3" spans="1:6" outlineLevel="2" x14ac:dyDescent="0.25"><c r="A3" t="s"><v>4</v></c><c r="B3" s="2"><v>2.5</v></c><c r="C3"><v>9</v></c><c r="D3"><f t="shared" si="0"/><v>22.5</v></c><c r="E3" s="3" t="d"><v>2023-03-15T12:00:00</v></c><c r="F3" t="e"><f>1/0</f><v>#DIV/0!</v></c></row><row r="4" ht="30" customHeight="1" x14ac:dyDescent="0.25"><c r="D4" cm="1"><f t="array" ref="D4">SUM(B2:B3*C2:C3)</f><v>35</v></c><c r="F4" t="str"><f>"Text "&amp;A2</f><v>Text Apple</v></c></row><row r="5" ht="30" x14ac:dyDescent="0.25"><c r="F5" s="4" t="s"><v>5</v></c></row><row r="6"><c r="F6" t="s"><v>6</v></c></row><row r="9"><c r="A9" s="5" t="s"><v>7</v></c><c r="B9" s="5"/><c r="C9" s="5"/></row><row r="10"><c r="A10" t="s"><v>9</v></c></row><row r="12"><c r="A12" s="6" t="s"><v>0</v></c><c r="B12" s="7"/></row></sheetData><sheetProtection sheet="0"/><mergeCells count="1"><mergeCell ref="A9:C9"/></mergeCells><conditionalFormatting sqref="B2:B6"><cfRule type="dataBar" priority="1"><dataBar><cfvo type="min"/><cfvo type="max"/><color rgb="FF638EC6"/></dataBar></cfRule></conditionalFormatting><dataValidations count="1"><dataValidation type="whole" allowBlank="1" showInputMessage="1" showErrorMessage="1" sqref="C2:C6"><formula1>0</formula1><formula2>100</formula2></dataValidation></dataValidations><hyperlinks><hyperlink ref="A12" r:id="rId1" display="Example"/></hyperlinks><printOptions horizontalCentered="1"/><pageMargins left="0.7" right="0.7" top="0.75" bottom="0.75" header="0.3" footer="0.3"/><pageSetup paperSize="9" orientation="landscape" r:id="rId2"/><headerFooter differentFirst="0"><oddHeader>&amp;CReport</oddHeader><oddFooter>&amp;RPage &amp;P of &amp;N</oddFooter></headerFooter><drawing r:id="rId3"/><legacyDrawing r:id="rId4"/><extLst><ext uri="{"{"}CCE6A557-97BC-4b89-ADB6-D9C93CAAB3DF{"}"}" xmlns:x14="http://schemas.microsoft.com/office/spreadsheetml/2009/9/main"><x14:dataValidations count="0" xmlns:xm="http://schemas.microsoft.com/office/excel/2006/main"/></ext></extLst></worksheet>
            """,
        ["xl/worksheets/_rels/sheet1.xml.rels"] = $"""
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{Rel}hyperlink" Target="https://example.com/" TargetMode="External"/><Relationship Id="rId2" Type="{Rel}printerSettings" Target="../printerSettings/printerSettings1.bin"/><Relationship Id="rId3" Type="{Rel}drawing" Target="../drawings/drawing1.xml"/><Relationship Id="rId4" Type="{Rel}vmlDrawing" Target="../drawings/vmlDrawing1.vml"/><Relationship Id="rId5" Type="{Rel}comments" Target="../comments1.xml"/></Relationships>
            """,
        ["xl/printerSettings/printerSettings1.bin"] = "printer settings bytes",
        ["xl/drawings/drawing1.xml"] = """<xdr:wsDr xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing"/>""",
        ["xl/drawings/vmlDrawing1.vml"] = """<xml xmlns:v="urn:schemas-microsoft-com:vml"/>""",
        ["xl/comments1.xml"] = $"""<comments xmlns="{Main}"><authors><author>Tester</author></authors><commentList><comment ref="C2" authorId="0"><text><t>A note</t></text></comment></commentList></comments>""",
        ["xl/worksheets/sheet2.xml"] = $"""<worksheet {Ns}><dimension ref="A1"/><sheetViews><sheetView workbookViewId="0"/></sheetViews><sheetFormatPr defaultRowHeight="15"/><sheetData><row r="1"><c r="A1" t="s"><v>8</v></c></row></sheetData><pageMargins left="0.7" right="0.7" top="0.75" bottom="0.75" header="0.3" footer="0.3"/></worksheet>""",
        ["xl/worksheets/sheet3.xml"] = $"""<worksheet {Ns}><dimension ref="A1:B3"/><sheetViews><sheetView workbookViewId="0"/></sheetViews><sheetFormatPr defaultRowHeight="15"/><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Name</t></is></c><c r="B1" t="inlineStr"><is><t>Score</t></is></c></row><row r="2"><c t="inlineStr"><is><t>Ann</t></is></c><c><v>7</v></c></row><row><c t="inlineStr"><is><t>Bo</t></is></c><c><v>9</v></c></row></sheetData><pageMargins left="0.7" right="0.7" top="0.75" bottom="0.75" header="0.3" footer="0.3"/><tableParts count="1"><tablePart r:id="rId1"/></tableParts></worksheet>""",
        ["xl/worksheets/_rels/sheet3.xml.rels"] = $"""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{Rel}table" Target="../tables/table1.xml"/></Relationships>""",
        ["xl/tables/table1.xml"] = $"""<table xmlns="{Main}" id="1" name="Table1" displayName="Table1" ref="A1:B3" totalsRowShown="0"><autoFilter ref="A1:B3"/><tableColumns count="2"><tableColumn id="1" name="Name"/><tableColumn id="2" name="Score"/></tableColumns><tableStyleInfo name="TableStyleMedium2" showFirstColumn="0" showLastColumn="0" showRowStripes="1" showColumnStripes="0"/></table>""",
        ["xl/chartsheets/sheet1.xml"] = $"""<chartsheet {Ns}><sheetPr/><sheetViews><sheetView workbookViewId="0"/></sheetViews><pageMargins left="0.7" right="0.7" top="0.75" bottom="0.75" header="0.3" footer="0.3"/></chartsheet>""",
    };

    /// <summary>The package of a set of parts, written as a ZIP.</summary>
    public static MemoryStream Package(Dictionary<string, string> parts, Dictionary<string, byte[]>? binary = null)
    {
        MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string text) in parts)
            {
                using Stream entry = zip.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(text.Trim()));
            }

            foreach ((string name, byte[] data) in binary ?? [])
            {
                using Stream entry = zip.CreateEntry(name).Open();
                entry.Write(data);
            }
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>The parts of a saved package by name, without the leading slash.</summary>
    public static Dictionary<string, byte[]> Parts(Stream package)
    {
        package.Position = 0;
        using ZipArchive zip = new(package, ZipArchiveMode.Read, leaveOpen: true);
        Dictionary<string, byte[]> parts = new(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using MemoryStream data = new();
            using (Stream input = entry.Open())
            {
                input.CopyTo(data);
            }

            parts[entry.FullName] = data.ToArray();
        }

        package.Position = 0;
        return parts;
    }

    public static XDocument Xml(Dictionary<string, byte[]> parts, string name) => XDocument.Load(new MemoryStream(parts[name]));

    /// <summary>Saves a workbook to a stream and loads it again.</summary>
    public static (Workbook Book, MemoryStream Package) RoundTrip(Workbook book, WorkbookSaveOptions? options = null)
    {
        MemoryStream saved = new();
        book.Save(saved, options);
        saved.Position = 0;
        Workbook loaded = Workbook.Load(saved);
        saved.Position = 0;
        return (loaded, saved);
    }

    public static Workbook LoadFeatures() => Workbook.Load(Package(Features()));
}
