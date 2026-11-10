using System.Xml.Linq;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents.DrawingML;
using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>DrawingML text bodies drawn in a shape's text rectangle: anchors, alignment, colours, bullets, vertical text and autofit.</summary>
public sealed class TextBodyPainterTests
{
    private const string Ns = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"";
    private static readonly Rgba8 White = new(255, 255, 255, 255);

    private static Raster<Rgba8> Draw(string body, string paragraphs, string? style = null, int width = 200, int height = 120, string bodyContent = "")
    {
        Raster<Rgba8> raster = Raster.Create(width, height, White);
        using RasterCanvas<Rgba8> canvas = new(raster);
        Document document = new();
        XElement txBody = XElement.Parse($"<a:txBody {Ns}><a:bodyPr {body}>{bodyContent}</a:bodyPr><a:lstStyle/>{paragraphs}</a:txBody>");
        XElement? styleElement = style is null ? null : XElement.Parse($"<a:style {Ns}>{style}</a:style>");
        ShapePaintContext context = new(document.Theme, Fonts: new FontProvider(document, new FontSettings { UseSystemFonts = false }));
        TextBodyPainter.Draw(canvas, new RectF(0, 0, width, height), 0, txBody, styleElement, context);
        return raster;
    }

    /// <summary>The box round the pixels <paramref name="match"/> picks: left, top, right, bottom; -1s when there are none.</summary>
    private static (int Left, int Top, int Right, int Bottom) Ink(Raster<Rgba8> raster, Func<Rgba8, bool>? match = null)
    {
        match ??= p => p.R < 160 || p.G < 160 || p.B < 160;
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (int y = 0; y < raster.Height; y++)
        {
            ReadOnlySpan<Rgba8> row = raster.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                if (match(row[x]))
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        return right < 0 ? (-1, -1, -1, -1) : (left, top, right, bottom);
    }

    private const string Hello = """<a:p><a:r><a:rPr lang="en-US" sz="1400"/><a:t>Hello</a:t></a:r></a:p>""";

    [Fact]
    public void AnchorsPlaceTheTextInTheBox()
    {
        using Raster<Rgba8> top = Draw("anchor=\"t\"", Hello);
        using Raster<Rgba8> middle = Draw("anchor=\"ctr\"", Hello);
        using Raster<Rgba8> bottom = Draw("anchor=\"b\"", Hello);

        Assert.InRange(Ink(top).Top, 3, 12);
        Assert.InRange((Ink(middle).Top + Ink(middle).Bottom) / 2, 52, 68);
        Assert.InRange(Ink(bottom).Bottom, 100, 117);
    }

    [Fact]
    public void ParagraphsAlignInTheirWidth()
    {
        using Raster<Rgba8> left = Draw("", Hello);
        using Raster<Rgba8> right = Draw("", Hello.Replace("<a:p>", "<a:p><a:pPr algn=\"r\"/>", StringComparison.Ordinal));
        using Raster<Rgba8> center = Draw("", Hello.Replace("<a:p>", "<a:p><a:pPr algn=\"ctr\"/>", StringComparison.Ordinal));

        Assert.InRange(Ink(left).Left, 7, 12);
        Assert.InRange(Ink(right).Right, 185, 193);
        (int l, _, int r, _) = Ink(center);
        Assert.InRange((l + r) / 2, 94, 106);
    }

    [Fact]
    public void RunsTakeTheirColourOrTheShapesFontReference()
    {
        using Raster<Rgba8> own = Draw("", """<a:p><a:r><a:rPr sz="2400"><a:solidFill><a:srgbClr val="FF0000"/></a:solidFill></a:rPr><a:t>Red</a:t></a:r></a:p>""");
        using Raster<Rgba8> referred = Draw("", """<a:p><a:r><a:rPr sz="2400"/><a:t>Blue</a:t></a:r></a:p>""", """<a:fontRef idx="minor"><a:srgbClr val="0000FF"/></a:fontRef>""");

        Assert.NotEqual(-1, Ink(own, p => p.R > 200 && p.G < 80 && p.B < 80).Left);
        Assert.NotEqual(-1, Ink(referred, p => p.B > 200 && p.R < 80 && p.G < 80).Left);
    }

    [Fact]
    public void BulletsHangLeftOfTheText()
    {
        const string Plain = """<a:p><a:pPr marL="342900" indent="-228600"><a:buNone/></a:pPr><a:r><a:rPr sz="1400"/><a:t>Item</a:t></a:r></a:p>""";
        using Raster<Rgba8> plain = Draw("", Plain);
        using Raster<Rgba8> bulleted = Draw("", Plain.Replace("<a:buNone/>", "<a:buChar char=\"•\"/>", StringComparison.Ordinal));

        // Without a bullet the first line starts at marL + indent (9 points past the 7.2-point inset); with one the bullet sits
        // there and the text moves on to marL, 18 points further.
        Assert.InRange(Ink(plain).Left, 14, 22);
        Assert.InRange(Ink(bulleted).Left, 14, 22);
        Assert.InRange(Ink(bulleted).Right - Ink(plain).Right, 15, 21);
    }

    [Fact]
    public void VerticalTextRunsDownTheBox()
    {
        const string Long = """<a:p><a:r><a:rPr sz="1400"/><a:t>Vertical words</a:t></a:r></a:p>""";
        (int l, int t, int r, int b) = Ink(Draw("vert=\"vert\" wrap=\"none\"", Long, height: 160));

        Assert.True(b - t > 2 * (r - l), $"{l} {t} {r} {b}");
    }

    [Fact]
    public void AutofitScalesTheText()
    {
        using Raster<Rgba8> full = Draw("", """<a:p><a:r><a:rPr sz="3600"/><a:t>Big</a:t></a:r></a:p>""");
        using Raster<Rgba8> half = Draw("", """<a:p><a:r><a:rPr sz="3600"/><a:t>Big</a:t></a:r></a:p>""", bodyContent: """<a:normAutofit fontScale="50000"/>""");

        (_, int t1, _, int b1) = Ink(full);
        (_, int t2, _, int b2) = Ink(half);
        Assert.InRange((b2 - t2) / (double)(b1 - t1), 0.4, 0.6);
    }

    [Fact]
    public void TextKeepsLinesAndBreaks()
    {
        XElement body = XElement.Parse($"""<a:txBody {Ns}><a:bodyPr/><a:p><a:r><a:t>One</a:t></a:r><a:br/><a:r><a:t>two</a:t></a:r></a:p><a:p><a:fld type="slidenum"><a:t>3</a:t></a:fld></a:p></a:txBody>""");

        Assert.Equal("One\ntwo\n3", TextBodyPainter.Text(body));
    }
}
