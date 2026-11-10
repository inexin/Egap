using System.Xml.Linq;

using Egami.Pixels;

using Egap.Documents.Docx;
using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>Word shapes (<c>wps</c>) of any preset and shape groups (<c>wpg</c>) drawn on pages through the DrawingML shape engine.</summary>
public sealed class WordShapeDrawingTests
{
    private const string Namespaces = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\""
        + " xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\""
        + " xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" xmlns:wpg=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\"";

    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };

    private static Document Load(string drawing)
    {
        OpcPackageWriter writer = new();
        string xml = $"<w:document {Namespaces}><w:body><w:p><w:r><w:drawing>{drawing}</w:drawing></w:r></w:p></w:body></w:document>";
        writer.AddXmlPart("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", XDocument.Parse(xml));
        writer.AddRelationship("/", "rId1", OpcRelationshipTypes.OfficeDocument, "word/document.xml");
        using MemoryStream stream = new();
        writer.Save(stream);
        stream.Position = 0;
        return DocxFormat.Load(stream);
    }

    private static string Inline(string graphic, long cx = 1270000, long cy = 1270000) =>
        $"""<wp:inline><wp:extent cx="{cx}" cy="{cy}"/><wp:docPr id="1" name="s"/><a:graphic>{graphic}</a:graphic></wp:inline>""";

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
    public void PresetShapesDrawTheirGeometry()
    {
        // A 100-point five-pointed star in red: far less than its box, far more than nothing.
        Document document = Load(Inline("""
            <a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"><wps:wsp>
              <wps:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="1270000" cy="1270000"/></a:xfrm><a:prstGeom prst="star5"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val="FF0000"/></a:solidFill><a:ln><a:noFill/></a:ln></wps:spPr>
              <wps:bodyPr/></wps:wsp></a:graphicData>
            """));

        Assert.Equal(ShapeKind.Other, document.Sections[0].Body.Paragraphs.First().Inlines.OfType<Shape>().Single().Kind);
        using Egami.Raster<Rgba8> page = document.Layout(Isolated).Pages[0].Render(72);
        int red = Count(page, p => p.R > 200 && p.G < 60 && p.B < 60);
        Assert.InRange(red, 3000, 6500);
    }

    [Fact]
    public void ThemeStyleReferencesGiveWordsDefaultLook()
    {
        // Word's inserted shape: no fill of its own, the theme's accent 1 through fillRef, a darker outline through lnRef.
        Document document = Load(Inline("""
            <a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"><wps:wsp>
              <wps:spPr><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></wps:spPr>
              <wps:style><a:lnRef idx="2"><a:schemeClr val="accent1"><a:shade val="15000"/></a:schemeClr></a:lnRef><a:fillRef idx="1"><a:schemeClr val="accent1"/></a:fillRef>
                <a:effectRef idx="0"><a:schemeClr val="accent1"/></a:effectRef><a:fontRef idx="minor"><a:schemeClr val="lt1"/></a:fontRef></wps:style>
              <wps:bodyPr/></wps:wsp></a:graphicData>
            """));

        using Egami.Raster<Rgba8> page = document.Layout(Isolated).Pages[0].Render(72);
        Rgba8 accent = document.Theme.Colors[ThemeColor.Accent1];
        Assert.True(Count(page, p => p == accent) > 8000);
    }

    [Fact]
    public void ChangedShapesDrawFromTheModel()
    {
        Document document = Load(Inline("""
            <a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape"><wps:wsp>
              <wps:spPr><a:prstGeom prst="star5"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val="FF0000"/></a:solidFill></wps:spPr>
              <wps:bodyPr/></wps:wsp></a:graphicData>
            """));
        Shape shape = document.Sections[0].Body.Paragraphs.First().Inlines.OfType<Shape>().Single();
        Assert.NotNull(DocxDrawings.ShapeMarkup(shape));

        shape.Fill = DocumentColor.FromHex("0000FF");

        Assert.Null(DocxDrawings.ShapeMarkup(shape));
    }

    [Fact]
    public void GroupsDrawTheirChildrenInPlace()
    {
        // A group 200 x 100 points whose child space is twice as large: a red rectangle on the left half, a blue ellipse on the right.
        Document document = Load(Inline(
            """
            <a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingGroup"><wpg:wgp>
              <wpg:cNvGrpSpPr/>
              <wpg:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="2540000" cy="1270000"/><a:chOff x="0" y="0"/><a:chExt cx="5080000" cy="2540000"/></a:xfrm></wpg:grpSpPr>
              <wps:wsp><wps:cNvPr id="2" name="a"/><wps:cNvSpPr/>
                <wps:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="2540000" cy="2540000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val="FF0000"/></a:solidFill><a:ln><a:noFill/></a:ln></wps:spPr><wps:bodyPr/></wps:wsp>
              <wps:wsp><wps:cNvPr id="3" name="b"/><wps:cNvSpPr/>
                <wps:spPr><a:xfrm><a:off x="2540000" y="0"/><a:ext cx="2540000" cy="2540000"/></a:xfrm><a:prstGeom prst="ellipse"><a:avLst/></a:prstGeom><a:solidFill><a:srgbClr val="0000FF"/></a:solidFill><a:ln><a:noFill/></a:ln></wps:spPr><wps:bodyPr/></wps:wsp>
            </wpg:wgp></a:graphicData>
            """, 2540000, 1270000));

        Island island = document.Sections[0].Body.Paragraphs.First().Inlines.OfType<Island>().Single();
        Assert.Equal(IslandKind.ShapeGroup, island.Kind);
        using Egami.Raster<Rgba8> page = document.Layout(Isolated).Pages[0].Render(72);
        int red = Count(page, p => p.R > 200 && p.G < 60 && p.B < 60);
        int blue = Count(page, p => p.B > 200 && p.R < 60 && p.G < 60);

        // A 100-point square and a 100-point circle (pi / 4 of it).
        Assert.InRange(red, 9500, 10500);
        Assert.InRange(blue, 7400, 8300);
    }
}
