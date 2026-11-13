using System.Xml.Linq;

using Egami.Pixels;

using Egap.Documents.Docx;
using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Tests;

/// <summary>SmartArt: drawn from the drawing Office stores beside the diagram, its text from the data model, a list without a drawing.</summary>
public sealed class SmartArtDrawingTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string DrawingType = "http://schemas.microsoft.com/office/2007/relationships/diagramDrawing";
    private const string DataType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/diagramData";

    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };

    /// <summary>
    /// Three points (Alpha, Beta with child Gamma) and, with <paramref name="drawing"/>, a stored drawing of a red rectangle on the
    /// frame's left half and a blue one on its right, the right one with text.
    /// </summary>
    private static Document Load(bool drawing)
    {
        OpcPackageWriter writer = new();
        string document = $"""
            <w:document xmlns:w="{W}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
              xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
              xmlns:dgm="http://schemas.openxmlformats.org/drawingml/2006/diagram"><w:body><w:p><w:r><w:drawing>
              <wp:inline><wp:extent cx="2540000" cy="1270000"/><wp:docPr id="1" name="Diagram 1"/><a:graphic>
                <a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/diagram"><dgm:relIds r:dm="rId2" r:lo="rId3" r:qs="rId4" r:cs="rId5"/></a:graphicData>
              </a:graphic></wp:inline></w:drawing></w:r></w:p></w:body></w:document>
            """;
        string extension = drawing
            ? """<dgm:extLst><a:ext uri="http://schemas.microsoft.com/office/drawing/2008/diagram"><dsp:dataModelExt xmlns:dsp="http://schemas.microsoft.com/office/drawing/2008/diagram" relId="rId6" minVer="http://schemas.openxmlformats.org/drawingml/2006/diagram"/></a:ext></dgm:extLst>"""
            : "";
        string data = $"""
            <dgm:dataModel xmlns:dgm="http://schemas.openxmlformats.org/drawingml/2006/diagram" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <dgm:ptLst>
                <dgm:pt modelId="0" type="doc"/>
                <dgm:pt modelId="2"><dgm:t><a:bodyPr/><a:p><a:r><a:t>Beta</a:t></a:r></a:p></dgm:t></dgm:pt>
                <dgm:pt modelId="1"><dgm:t><a:bodyPr/><a:p><a:r><a:t>Alpha</a:t></a:r></a:p></dgm:t></dgm:pt>
                <dgm:pt modelId="3"><dgm:t><a:bodyPr/><a:p><a:r><a:t>Gamma</a:t></a:r></a:p></dgm:t></dgm:pt>
                <dgm:pt modelId="9" type="sibTrans"><dgm:t><a:bodyPr/><a:p><a:r><a:t>never</a:t></a:r></a:p></dgm:t></dgm:pt>
              </dgm:ptLst>
              <dgm:cxnLst>
                <dgm:cxn modelId="10" srcId="0" destId="1" srcOrd="0" destOrd="0"/>
                <dgm:cxn modelId="11" srcId="0" destId="2" srcOrd="1" destOrd="0"/>
                <dgm:cxn modelId="12" srcId="2" destId="3" srcOrd="0" destOrd="0"/>
              </dgm:cxnLst>
              {extension}
            </dgm:dataModel>
            """;
        string tree = """
            <dsp:drawing xmlns:dsp="http://schemas.microsoft.com/office/drawing/2008/diagram" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <dsp:spTree><dsp:nvGrpSpPr><dsp:cNvPr id="0" name=""/><dsp:cNvGrpSpPr/></dsp:nvGrpSpPr><dsp:grpSpPr/>
                <dsp:sp modelId="{1}"><dsp:nvSpPr><dsp:cNvPr id="0" name=""/><dsp:cNvSpPr/></dsp:nvSpPr>
                  <dsp:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="1270000" cy="1270000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val="FF0000"/></a:solidFill><a:ln><a:noFill/></a:ln></dsp:spPr></dsp:sp>
                <dsp:sp modelId="{2}"><dsp:nvSpPr><dsp:cNvPr id="0" name=""/><dsp:cNvSpPr/></dsp:nvSpPr>
                  <dsp:spPr><a:xfrm><a:off x="1270000" y="0"/><a:ext cx="1270000" cy="1270000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val="0000FF"/></a:solidFill><a:ln><a:noFill/></a:ln></dsp:spPr>
                  <dsp:txBody><a:bodyPr anchor="ctr"/><a:lstStyle/><a:p><a:pPr algn="ctr"/><a:r><a:rPr sz="2000"><a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill></a:rPr><a:t>Beta</a:t></a:r></a:p></dsp:txBody>
                  <dsp:txXfrm><a:off x="1270000" y="0"/><a:ext cx="1270000" cy="1270000"/></dsp:txXfrm></dsp:sp>
              </dsp:spTree></dsp:drawing>
            """;
        writer.AddXmlPart("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", XDocument.Parse(document));
        writer.AddXmlPart("/word/diagrams/data1.xml", "application/vnd.openxmlformats-officedocument.drawingml.diagramData+xml", XDocument.Parse(data));
        writer.AddRelationship("/", "rId1", OpcRelationshipTypes.OfficeDocument, "word/document.xml");
        writer.AddRelationship("/word/document.xml", "rId2", DataType, "diagrams/data1.xml");
        if (drawing)
        {
            writer.AddXmlPart("/word/diagrams/drawing1.xml", "application/vnd.ms-office.drawingml.diagramDrawing+xml", XDocument.Parse(tree));
            writer.AddRelationship("/word/document.xml", "rId6", DrawingType, "diagrams/drawing1.xml");
        }

        using MemoryStream stream = new();
        writer.Save(stream);
        stream.Position = 0;
        return DocxFormat.Load(stream);
    }

    private static int Count(Egami.Raster<Rgba8> page, Func<Rgba8, bool> match)
    {
        int count = 0;
        for (int y = 0; y < page.Height; y++)
        {
            foreach (Rgba8 pixel in page.Row(y))
            {
                count += match(pixel) ? 1 : 0;
            }
        }

        return count;
    }

    [Fact]
    public void TextComesFromTheDataModelInOutlineOrder()
    {
        Island island = Load(drawing: true).Sections[0].Body.Paragraphs.First().Inlines.OfType<Island>().Single();

        Assert.Equal(IslandKind.SmartArt, island.Kind);
        Assert.Equal("Alpha\nBeta\nGamma", island.Text);
    }

    [Fact]
    public void TheStoredDrawingDrawsInTheFrame()
    {
        DocumentLayout layout = Load(drawing: true).Layout(Isolated);
        using Egami.Raster<Rgba8> page = layout.Pages[0].Render(72);

        // Two 100-point squares; the blue one has white text on it.
        Assert.InRange(Count(page, p => p.R > 200 && p.G < 60 && p.B < 60), 9500, 10500);
        int blue = Count(page, p => p.B > 200 && p.R < 60 && p.G < 60);
        Assert.InRange(blue, 8000, 9990);
        Assert.Empty(layout.Warnings);
    }

    [Fact]
    public void WithoutADrawingTheTextDrawsAsAListWithAWarning()
    {
        DocumentLayout layout = Load(drawing: false).Layout(Isolated);
        using Egami.Raster<Rgba8> page = layout.Pages[0].Render(72);

        Rgba8 accent1 = layout.Document.Theme.Colors[ThemeColor.Accent1];
        Rgba8 accent2 = layout.Document.Theme.Colors[ThemeColor.Accent2];
        Assert.True(Count(page, p => p == accent1) > 2000);
        Assert.True(Count(page, p => p == accent2) > 2000);
        LayoutWarning warning = Assert.Single(layout.Warnings);
        Assert.Equal(LayoutWarningKind.SmartArtDrawnAsList, warning.Kind);
        Assert.Equal(0, warning.PageIndex);
    }

    [Fact]
    public void ADiagramIsWrittenBackAsRead()
    {
        Document document = Load(drawing: true);
        using MemoryStream saved = new();
        DocxFormat.Save(document, saved);
        saved.Position = 0;

        Island island = DocxFormat.Load(saved).Sections[0].Body.Paragraphs.First().Inlines.OfType<Island>().Single();
        Assert.Equal("Alpha\nBeta\nGamma", island.Text);
        Assert.NotNull(DocxDrawings.DiagramTree(island));
    }

    [Fact]
    public void TaggedPdfGivesTheDiagramItsTextAsAlternativeText()
    {
        using MemoryStream stream = new();
        Load(drawing: true).SaveAsPdf(stream, new PdfExportOptions { Tagged = true, Layout = Isolated });
        using PdfDocument pdf = PdfDocument.Open(stream.ToArray());

        PdfStructureElement root = Assert.IsType<PdfStructureElement>(pdf.Structure.Root);
        PdfStructureElement figure = root.DescendantsAndSelf().Single(e => e.Type == PdfStructureType.Figure);
        Assert.Equal("Alpha; Beta; Gamma", figure.AlternateText);
    }
}
