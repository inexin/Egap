using System.Xml.Linq;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents.DrawingML;

namespace Egap.Documents.Tests;

/// <summary>The DrawingML shape painter: each fill and line kind, theme style references and effects.</summary>
public sealed class ShapePainterTests
{
    private const string Ns = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
    private static readonly Rgba8 White = new(255, 255, 255, 255);

    private static Raster<Rgba8> Draw(string spPr, string? style = null, Theme? theme = null, int width = 100, int height = 60, RectF? box = null)
    {
        Raster<Rgba8> raster = Raster.Create(width, height, White);
        using RasterCanvas<Rgba8> canvas = new(raster);
        XElement properties = XElement.Parse($"<a:spPr {Ns}>{spPr}</a:spPr>");
        XElement? styleElement = style is null ? null : XElement.Parse($"<a:style {Ns}>{style}</a:style>");
        ShapePainter.Draw(canvas, box ?? new RectF(10, 10, 80, 40), properties, styleElement, new ShapePaintContext(theme ?? new Document().Theme));
        return raster;
    }

    private static Rgba8 At(Raster<Rgba8> raster, int x, int y) => raster.Row(y)[x];

    [Fact]
    public void SolidFillFillsTheGeometry()
    {
        using Raster<Rgba8> raster = Draw("""<a:prstGeom prst="ellipse"/><a:solidFill><a:srgbClr val="FF0000"/></a:solidFill><a:ln><a:noFill/></a:ln>""");

        Assert.Equal(new Rgba8(255, 0, 0, 255), At(raster, 50, 30));
        Assert.Equal(White, At(raster, 12, 12));
    }

    [Fact]
    public void ThemeColoursAndChangesResolve()
    {
        using Raster<Rgba8> raster = Draw("""<a:solidFill><a:schemeClr val="accent1"><a:lumMod val="50000"/></a:schemeClr></a:solidFill>""");

        Rgba8 accent = new Document().Theme.Colors[ThemeColor.Accent1];
        Rgba8 drawn = At(raster, 50, 30);
        Assert.True(drawn.B < accent.B && drawn.R <= accent.R, drawn.ToString());
    }

    [Fact]
    public void LinearGradientRunsAlongItsAngle()
    {
        using Raster<Rgba8> raster = Draw(
            """<a:gradFill><a:gsLst><a:gs pos="0"><a:srgbClr val="000000"/></a:gs><a:gs pos="100000"><a:srgbClr val="FFFFFF"/></a:gs></a:gsLst><a:lin ang="0"/></a:gradFill><a:ln><a:noFill/></a:ln>""");

        Assert.True(At(raster, 12, 30).R < 40);
        Assert.True(At(raster, 87, 30).R > 215);
        Assert.InRange(At(raster, 50, 30).R, 100, 155);
    }

    [Fact]
    public void PathGradientRunsFromTheCentre()
    {
        using Raster<Rgba8> raster = Draw(
            """<a:gradFill><a:gsLst><a:gs pos="0"><a:srgbClr val="000000"/></a:gs><a:gs pos="100000"><a:srgbClr val="FFFFFF"/></a:gs></a:gsLst><a:path path="circle"/></a:gradFill><a:ln><a:noFill/></a:ln>""");

        Assert.True(At(raster, 50, 30).R < 20);
        Assert.True(At(raster, 11, 11).R > At(raster, 40, 30).R);
    }

    [Fact]
    public void PatternFillBlendsItsInks()
    {
        using Raster<Rgba8> raster = Draw(
            """<a:pattFill prst="pct50"><a:fgClr><a:srgbClr val="000000"/></a:fgClr><a:bgClr><a:srgbClr val="FFFFFF"/></a:bgClr></a:pattFill><a:ln><a:noFill/></a:ln>""");

        Assert.InRange(At(raster, 50, 30).R, 120, 135);
    }

    [Fact]
    public void PictureFillDrawsThroughTheContext()
    {
        using Raster<Rgba8> raster = Raster.Create(100, 60, White);
        using RasterCanvas<Rgba8> canvas = new(raster);
        XElement spPr = XElement.Parse(
            $"""<a:spPr {Ns} xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><a:blipFill><a:blip r:embed="rId7"/></a:blipFill></a:spPr>""");
        string? asked = null;
        ShapePainter.Draw(canvas, new RectF(10, 10, 80, 40), spPr, null, new ShapePaintContext(new Document().Theme, (c, id, rect) =>
        {
            asked = id;
            c.FillRect(rect, new SolidPaint(new Rgba8(0, 0, 255, 255)));
        }));

        Assert.Equal("rId7", asked);
        Assert.Equal(new Rgba8(0, 0, 255, 255), At(raster, 50, 30));
    }

    [Fact]
    public void LinesTakeTheirWidthAndColour()
    {
        using Raster<Rgba8> raster = Draw("""<a:noFill/><a:ln w="76200"><a:solidFill><a:srgbClr val="00FF00"/></a:solidFill></a:ln>""");

        // A 6-point line on the box's left edge, at x = 10, reaches from 7 to 13.
        Assert.Equal(new Rgba8(0, 255, 0, 255), At(raster, 8, 30));
        Assert.Equal(new Rgba8(0, 255, 0, 255), At(raster, 12, 30));
        Assert.Equal(White, At(raster, 15, 30));
        Assert.Equal(White, At(raster, 50, 30));
    }

    [Fact]
    public void DashedLinesLeaveGaps()
    {
        using Raster<Rgba8> raster = Draw("""<a:prstGeom prst="line"/><a:ln w="25400"><a:solidFill><a:srgbClr val="000000"/></a:solidFill><a:prstDash val="dash"/></a:ln>""", box: new RectF(0, 30, 100, 0));

        int ink = 0;
        foreach (Rgba8 pixel in raster.Row(30))
        {
            ink += pixel.R < 128 ? 1 : 0;
        }

        // Dashes of four widths and gaps of three: about four sevenths inked.
        Assert.InRange(ink, 50, 66);
    }

    [Fact]
    public void CustomDashesUseThousandthsOfAPercent()
    {
        XElement line = XElement.Parse($"""<a:ln {Ns}><a:custDash><a:ds d="300000" sp="100000"/></a:custDash></a:ln>""");

        Assert.Equal([6f, 2f], ShapePainter.Dash(line, 2)!);
    }

    [Fact]
    public void DoubleLinesLeaveAGapInTheMiddle()
    {
        using Raster<Rgba8> raster = Draw("""<a:prstGeom prst="line"/><a:ln w="114300" cmpd="dbl"><a:solidFill><a:srgbClr val="000000"/></a:solidFill></a:ln>""", box: new RectF(0, 30, 100, 0));

        // A 9-point double line: ink 3 points either side of a 3-point gap.
        Assert.True(At(raster, 50, 26).R < 60);
        Assert.True(At(raster, 50, 30).R > 200);
        Assert.True(At(raster, 50, 33).R < 60);
    }

    [Fact]
    public void ArrowheadsDrawAtTheEnds()
    {
        using Raster<Rgba8> plain = Draw("""<a:prstGeom prst="line"/><a:ln w="12700"><a:solidFill><a:srgbClr val="000000"/></a:solidFill></a:ln>""", box: new RectF(10, 30, 80, 0));
        using Raster<Rgba8> arrow = Draw("""<a:prstGeom prst="line"/><a:ln w="12700"><a:solidFill><a:srgbClr val="000000"/></a:solidFill><a:tailEnd type="triangle" w="lg" len="lg"/></a:ln>""", box: new RectF(10, 30, 80, 0));

        Assert.Equal(White, At(plain, 86, 28));
        Assert.True(At(arrow, 86, 28).R < 128);
    }

    [Fact]
    public void ThemeStyleReferencesFillAndOutline()
    {
        Document document = new();
        document.Theme.Read(XElement.Parse(
            """
            <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Test">
              <a:themeElements>
                <a:fmtScheme name="Test">
                  <a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"><a:shade val="50000"/></a:schemeClr></a:solidFill></a:fillStyleLst>
                  <a:lnStyleLst><a:ln w="76200"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst>
                  <a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>
                  <a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:bgFillStyleLst>
                </a:fmtScheme>
              </a:themeElements>
            </a:theme>
            """));

        using Raster<Rgba8> raster = Draw(
            "",
            """<a:lnRef idx="1"><a:srgbClr val="0000FF"/></a:lnRef><a:fillRef idx="1"><a:srgbClr val="FF0000"/></a:fillRef><a:effectRef idx="0"><a:srgbClr val="000000"/></a:effectRef>""",
            document.Theme);

        Assert.Equal(new Rgba8(255, 0, 0, 255), At(raster, 50, 30));
        Assert.Equal(new Rgba8(0, 0, 255, 255), At(raster, 10, 30));
    }

    [Fact]
    public void OwnLineSettingsOverTheTheme()
    {
        Document document = new();
        (XElement? line, Rgba8? placeholder) = ShapePainter.LineOf(
            XElement.Parse($"""<a:spPr {Ns}><a:ln w="25400"><a:prstDash val="dot"/></a:ln></a:spPr>"""),
            XElement.Parse($"""<a:style {Ns}><a:lnRef idx="2"><a:srgbClr val="123456"/></a:lnRef></a:style>"""),
            document.Theme);

        Assert.Equal("25400", (string?)line!.Attribute("w"));
        Assert.Equal("dot", (string?)line.Element(DrawingNs.A + "prstDash")?.Attribute("val"));
        Assert.NotNull(line.Element(DrawingNs.A + "solidFill"));
        Assert.Equal(Rgba8.FromHex("123456"), placeholder);
    }

    [Fact]
    public void OuterShadowFallsBehind()
    {
        using Raster<Rgba8> raster = Draw(
            """<a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill><a:ln><a:noFill/></a:ln><a:effectLst><a:outerShdw dist="76200" dir="0"><a:srgbClr val="000000"/></a:outerShdw></a:effectLst>""");

        // Moved 6 points right: dark past the right edge, white inside.
        Assert.True(At(raster, 93, 30).R < 60);
        Assert.Equal(White, At(raster, 50, 30));
    }

    [Fact]
    public void GlowSurroundsTheShape()
    {
        using Raster<Rgba8> raster = Draw(
            """<a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill><a:ln><a:noFill/></a:ln><a:effectLst><a:glow rad="101600"><a:srgbClr val="FF0000"/></a:glow></a:effectLst>""");

        Rgba8 outside = At(raster, 50, 7);
        Assert.True(outside.G < 250 && outside.R > outside.G, outside.ToString());
    }

    [Fact]
    public void SoftEdgeFadesTheOutline()
    {
        using Raster<Rgba8> raster = Draw(
            """<a:solidFill><a:srgbClr val="000000"/></a:solidFill><a:ln><a:noFill/></a:ln><a:effectLst><a:softEdge rad="127000"/></a:effectLst>""");

        Assert.True(At(raster, 50, 30).R < 30, At(raster, 50, 30).ToString());
        Assert.True(At(raster, 11, 30).R > 100, At(raster, 11, 30).ToString());
    }

    [Fact]
    public void FlipsAndRotationTurnTheShape()
    {
        using Raster<Rgba8> right = Draw("""<a:prstGeom prst="rtTriangle"/><a:solidFill><a:srgbClr val="000000"/></a:solidFill><a:ln><a:noFill/></a:ln>""");
        using Raster<Rgba8> flipped = Draw("""<a:xfrm flipH="1"/><a:prstGeom prst="rtTriangle"/><a:solidFill><a:srgbClr val="000000"/></a:solidFill><a:ln><a:noFill/></a:ln>""");

        // The right triangle has its right angle bottom left; flipped, bottom right.
        Assert.True(At(right, 13, 47).R < 60);
        Assert.Equal(White, At(right, 87, 15));
        Assert.True(At(flipped, 87, 47).R < 60);
        Assert.Equal(White, At(flipped, 13, 15));
    }

    [Fact]
    public void PathsWithTheirOwnShadeDrawLighterOrDarker()
    {
        using Raster<Rgba8> raster = Draw("""<a:prstGeom prst="can"/><a:solidFill><a:srgbClr val="808080"/></a:solidFill>""", width: 100, height: 100, box: new RectF(20, 5, 60, 90));

        // The can's top is its lighter face.
        Assert.True(At(raster, 50, 10).R > At(raster, 50, 60).R);
    }
}
