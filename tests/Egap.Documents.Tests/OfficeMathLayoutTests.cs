using System.Xml.Linq;

using Egami;
using Egami.Fonts;
using Egami.Fonts.Substitutes;
using Egami.Pixels;

using Egap.Documents.Layout;
using Egap.Documents.OfficeMath;

namespace Egap.Documents.Tests;

/// <summary>
/// Math layout (Build Plan tasks 12 to 14): math alphabets, atom spacing, styles, fractions, scripts, radicals, stretchy
/// delimiters and operators through variants and assemblies, and equations in paragraphs, inline on the baseline and in
/// display lines of their own.
/// </summary>
public sealed class OfficeMathLayoutTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = FontSettings.Portable };

    private static readonly Typeface Stix = SubstituteFonts.Find("Cambria Math", FontStyle.Normal)!;

    private static MathLayout Layout(float size = 10) => new(Stix, Stix.Math!, MathSettings.Default, size);

    private static MathBox Inline(string content, float size = 10) =>
        Layout(size).Zone((MathZone)Omml.Read(OfficeMathTests.Zone(content)), display: false);

    private static MathBox Display(string content, float size = 10) =>
        Layout(size).Zone((MathZone)Omml.Read(OfficeMathTests.Zone(content)), display: true);

    private static IEnumerable<LayoutBox> All(IEnumerable<LayoutBox> boxes) => boxes.SelectMany(b => (IEnumerable<LayoutBox>)[b, .. All(b.Children)]);

    private static string Run(string text) => $"<m:r><m:t>{text}</m:t></m:r>";

    [Theory]
    [InlineData('x', null, null, 0x1D465)]
    [InlineData('h', null, null, 0x210E)]
    [InlineData('x', "p", null, 'x')]
    [InlineData('A', "b", null, 0x1D400)]
    [InlineData('A', "bi", null, 0x1D468)]
    [InlineData('R', "p", "double-struck", 0x211D)]
    [InlineData('L', null, "script", 0x2112)]
    [InlineData('g', null, "fraktur", 0x1D524)]
    [InlineData('1', null, "double-struck", 0x1D7D9)]
    [InlineData('3', "b", null, 0x1D7D1)]
    [InlineData(0x3B1, null, null, 0x1D6FC)]
    [InlineData(0x3A9, "b", null, 0x1D6C0)]
    [InlineData(0x3C9, "p", null, 0x3C9)]
    [InlineData('-', null, null, 0x2212)]
    [InlineData('+', null, null, '+')]
    public void RunsMapToMathAlphanumerics(int c, string? style, string? script, int expected)
    {
        Assert.Equal(expected, MathAlphabets.Map(c, style, script));
    }

    [Fact]
    public void SpacingFollowsAtomClasses()
    {
        float plain = Inline(Run("ab")).Width;
        float binary = Inline(Run("a+b")).Width;
        float relation = Inline(Run("a=b")).Width;
        float plus = Inline(Run("+")).Width;
        float equals = Inline(Run("=")).Width;

        // a+b has two medium spaces (4/18 em), a=b two thick ones (5/18 em).
        Assert.Equal(plain + plus + 2 * 10 * 4 / 18f, binary, 0.3f);
        Assert.Equal(plain + equals + 2 * 10 * 5 / 18f, relation, 0.3f);

        // A leading plus is unary: no space after it.
        Assert.Equal(Inline(Run("+a")).Width, plus + Inline(Run("a")).Width, 0.3f);
    }

    [Fact]
    public void ScriptsAreSmallerAndShifted()
    {
        MathBox sup = Inline($"<m:sSup><m:e>{Run("x")}</m:e><m:sup>{Run("2")}</m:sup></m:sSup>");
        MathBox sub = Inline($"<m:sSub><m:e>{Run("x")}</m:e><m:sub>{Run("2")}</m:sub></m:sSub>");
        MathBox x = Inline(Run("x"));
        MathBox two = Inline(Run("2"));

        Assert.True(sup.Ascent > x.Ascent);
        Assert.True(sub.Descent > x.Descent);
        (MathBox script, float _, float supY) = sup.Descendants().Last();
        Assert.True(supY < 0);
        Assert.True(script.Width < two.Width);
        Assert.True(sub.Descendants().Last().Y > 0);
    }

    [Fact]
    public void FractionsStackOnTheAxisWithABar()
    {
        string fraction = $"<m:f><m:num>{Run("1")}</m:num><m:den>{Run("2")}</m:den></m:f>";
        MathBox inline = Inline(fraction);
        MathBox display = Display(fraction);
        MathItem.Rule bar = Assert.Single(inline.Items.OfType<MathItem.Rule>());
        float axis = Stix.Math!.Constants.AxisHeight * 10 / Stix.UnitsPerEm;

        Assert.Equal(-axis, bar.Y + bar.Height / 2, 0.01f);
        Assert.True(display.Height > inline.Height);

        MathBox noBar = Inline($"<m:f><m:fPr><m:type m:val=\"noBar\"/></m:fPr><m:num>{Run("n")}</m:num><m:den>{Run("k")}</m:den></m:f>");
        Assert.Empty(noBar.Items.OfType<MathItem.Rule>());

        MathBox linear = Inline($"<m:f><m:fPr><m:type m:val=\"lin\"/></m:fPr><m:num>{Run("1")}</m:num><m:den>{Run("2")}</m:den></m:f>");
        Assert.Equal(Inline(Run("1/2")).Width, linear.Width, 0.01f);
    }

    [Fact]
    public void RadicalsCoverTheirBody()
    {
        MathBox tall = Inline($"<m:rad><m:radPr><m:degHide m:val=\"1\"/></m:radPr><m:deg/><m:e><m:f><m:num>{Run("1")}</m:num><m:den>{Run("2")}</m:den></m:f></m:e></m:rad>");
        MathBox fraction = Inline($"<m:f><m:num>{Run("1")}</m:num><m:den>{Run("2")}</m:den></m:f>");
        MathItem.Rule rule = Assert.Single(tall.Items.OfType<MathItem.Rule>());

        Assert.True(-rule.Y > fraction.Ascent);
        Assert.True(tall.Width > fraction.Width);

        MathBox degree = Inline($"<m:rad><m:deg>{Run("3")}</m:deg><m:e>{Run("x")}</m:e></m:rad>");
        Assert.True(degree.Width > Inline($"<m:rad><m:radPr><m:degHide m:val=\"1\"/></m:radPr><m:deg/><m:e>{Run("x")}</m:e></m:rad>").Width - 0.01f);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public void DelimitersStretchToEverySize(float target)
    {
        MathLayout layout = Layout();
        foreach (int c in (ReadOnlySpan<int>)['(', ')', '[', '{', '|', 0x221A, 0x222B])
        {
            MathBox box = layout.StretchVertical(Stix.GetGlyph(c), target, 10);
            Assert.True(box.Height >= target * 0.999f || target > 100 && Stix.Math!.GetAssembly(Stix.GetGlyph(c), MathDirection.Vertical) is null,
                $"U+{c:X4} at {target}: {box.Height}");
        }

        foreach (int c in (ReadOnlySpan<int>)[0x23DF, 0x23DE, 0x2192])
        {
            MathBox box = layout.StretchHorizontal(Stix.GetGlyph(c), target, 10);
            Assert.True(box.Width >= target * 0.999f || Stix.Math!.GetAssembly(Stix.GetGlyph(c), MathDirection.Horizontal) is null,
                $"U+{c:X4} at {target}: {box.Width}");
        }
    }

    [Fact]
    public void AssembliesOverlapAtLeastTheMinimum()
    {
        TypefaceMath math = Stix.Math!;
        MathGlyphAssembly assembly = math.GetAssembly(Stix.GetGlyph('('), MathDirection.Vertical)!;
        List<(ushort Glyph, float At, float Advance)> parts = Layout().Assemble(assembly, 5000)!;

        Assert.True(parts.Count > assembly.Parts.Count);
        for (int i = 1; i < parts.Count; i++)
        {
            float overlap = parts[i - 1].At + parts[i - 1].Advance - parts[i].At;
            Assert.True(overlap >= math.MinConnectorOverlap - 0.01f);
        }

        Assert.True(parts[^1].At + parts[^1].Advance >= 5000 - 0.01f);
    }

    [Fact]
    public void DelimitersGrowAroundTallContent()
    {
        string fraction = $"<m:f><m:num>{Run("1")}</m:num><m:den>{Run("2")}</m:den></m:f>";
        MathBox grown = Display($"<m:d><m:e>{fraction}</m:e></m:d>");
        MathBox fixedSize = Display($"<m:d><m:dPr><m:grow m:val=\"0\"/></m:dPr><m:e>{fraction}</m:e></m:d>");
        MathBox plain = Display($"<m:d><m:e>{Run("x")}</m:e></m:d>");

        Assert.True(grown.Height > fixedSize.Height - 0.01f);
        Assert.True(grown.Height > plain.Height);
    }

    [Fact]
    public void DisplayOperatorsAreLargerWithLimitsOverAndUnder()
    {
        string sum = $"<m:nary><m:naryPr><m:chr m:val=\"&#x2211;\"/><m:limLoc m:val=\"undOvr\"/></m:naryPr><m:sub>{Run("i")}</m:sub><m:sup>{Run("n")}</m:sup><m:e>{Run("i")}</m:e></m:nary>";
        MathBox display = Display(sum);
        MathBox inline = Inline(sum);

        Assert.True(display.Height > inline.Height);

        // Inline, the limits sit beside the operator, so the box is wider than the operator and its body alone.
        Assert.True(inline.Width > Inline($"<m:nary><m:naryPr><m:chr m:val=\"&#x2211;\"/><m:subHide m:val=\"1\"/><m:supHide m:val=\"1\"/></m:naryPr><m:sub/><m:sup/><m:e>{Run("i")}</m:e></m:nary>").Width);
    }

    [Fact]
    public void EveryObjectLaysOutAndScalesWithSize()
    {
        foreach (XElement zone in OfficeMathTests.EquationZones())
        {
            if (zone.ToString().Contains("cSp", StringComparison.Ordinal) || zone.ToString().Contains("rSp", StringComparison.Ordinal))
            {
                // Matrix column widths and array row spacing in twips don't scale with the font.
                continue;
            }

            MathZone tree = (MathZone)Omml.Read(zone);
            MathBox small = Layout(10).Zone(tree, display: false);
            MathBox large = Layout(20).Zone(tree, display: false);

            Assert.True(small.Width >= 0 && small.Height >= 0, zone.ToString());
            Assert.Equal(small.Width * 2, large.Width, small.Width * 0.02f + 0.01f);
        }
    }

    [Fact]
    public void PhantomsTakeSpaceButDrawNothing()
    {
        MathBox hidden = Inline($"<m:phant><m:phantPr><m:show m:val=\"0\"/></m:phantPr><m:e>{Run("q")}</m:e></m:phant>");
        MathBox noWidth = Inline($"<m:phant><m:phantPr><m:zeroWid/></m:phantPr><m:e>{Run("q")}</m:e></m:phant>");

        Assert.True(hidden.Hidden);
        Assert.Equal(Inline(Run("q")).Width, hidden.Width, 0.01f);
        Assert.Equal(0, noWidth.Width);
    }

    [Fact]
    public void EquationArraysAlignAtTheirMarks()
    {
        MathBox array = Inline($"<m:eqArr><m:e>{Run("x&amp;=1")}</m:e><m:e>{Run("yyy&amp;=2")}</m:e></m:eqArr>");
        float equals = Inline(Run("=")).Width;

        Assert.True(array.Height > Inline(Run("x=1")).Height * 1.5f);

        // Both equals signs start at the same x: the widest left part (yyy) plus a thick space.
        float[] xs = [.. Glyphs(array, 0, 0).Where(g => g.Glyph == Stix.GetGlyph('=')).Select(g => g.X)];
        Assert.Equal(2, xs.Length);
        Assert.Equal(xs[0], xs[1], 0.01f);
        Assert.True(array.Width > Inline(Run("yyy=2")).Width - equals * 0.1f);
    }

    private static IEnumerable<(ushort Glyph, float X)> Glyphs(MathBox box, float x, float y)
    {
        foreach (MathItem item in box.Items)
        {
            if (item is MathItem.Glyphs glyphs)
            {
                for (int i = 0; i < glyphs.Ids.Length; i++)
                {
                    yield return (glyphs.Ids[i], x + glyphs.Positions[i].X);
                }
            }
            else if (item is MathItem.Child child)
            {
                foreach ((ushort, float) inner in Glyphs(child.Box, x + child.X, y + child.Y))
                {
                    yield return inner;
                }
            }
        }
    }

    [Fact]
    public void InlineEquationsSitOnTheBaseline()
    {
        Document document = OfficeMathTests.LoadDocx(
            "<w:p><w:r><w:t>Area </w:t></w:r><m:oMath><m:f><m:num><m:r><m:t>a</m:t></m:r></m:num><m:den><m:r><m:t>b</m:t></m:r></m:den></m:f></m:oMath><w:r><w:t> here.</w:t></w:r></w:p>");
        PageLayout page = document.Layout(Isolated).Pages[0];
        ParagraphBox paragraph = All(page.Boxes).OfType<ParagraphBox>().First();
        ObjectBox equation = paragraph.Children.OfType<ObjectBox>().Single();
        LineBox line = Assert.Single(paragraph.Lines);
        MathBox box = MathRendering.Box((Island)equation.Node!)!;

        Assert.Equal(line.Baseline, equation.Bounds.Y + box.Ascent, 0.01);
        Assert.Equal(box.Width, equation.Bounds.Width, 0.01);

        // The fraction makes the line taller than a line of text alone.
        Document plain = OfficeMathTests.LoadDocx("<w:p><w:r><w:t>Area here.</w:t></w:r></w:p>");
        Assert.True(line.Bounds.Height > All(plain.Layout(Isolated).Pages[0].Boxes).OfType<ParagraphBox>().First().Lines[0].Bounds.Height);
    }

    [Theory]
    [InlineData(null, null, "center")]
    [InlineData("left", null, "left")]
    [InlineData(null, "<m:mathPr><m:defJc m:val=\"right\"/></m:mathPr>", "right")]
    [InlineData(null, "<m:mathPr><m:dispDef m:val=\"0\"/><m:defJc m:val=\"right\"/></m:mathPr>", "center")]
    public void DisplayEquationsTakeTheirOwnLine(string? jc, string? mathPr, string expected)
    {
        string paraPr = jc is null ? "" : $"<m:oMathParaPr><m:jc m:val=\"{jc}\"/></m:oMathParaPr>";
        Document document = OfficeMathTests.LoadDocx(
            $"<w:p><w:r><w:t>Before</w:t></w:r><m:oMathPara>{paraPr}<m:oMath><m:r><m:t>E=mc</m:t></m:r></m:oMath></m:oMathPara><w:r><w:t>after</w:t></w:r></w:p>",
            mathPr);
        PageLayout page = document.Layout(Isolated).Pages[0];
        ParagraphBox paragraph = All(page.Boxes).OfType<ParagraphBox>().First();
        ObjectBox equation = paragraph.Children.OfType<ObjectBox>().Single();

        Assert.Equal(3, paragraph.Lines.Count);
        LineBox middle = paragraph.Lines[1];
        double left = equation.Bounds.X - middle.Bounds.X;
        double right = middle.Bounds.Right - equation.Bounds.Right;
        switch (expected)
        {
            case "left":
                Assert.Equal(0, left, 0.01);
                break;
            case "right":
                Assert.Equal(0, right, 0.01);
                break;
            default:
                Assert.Equal(left, right, 0.5);
                break;
        }
    }

    [Fact]
    public void EquationsDraw()
    {
        Document document = OfficeMathTests.LoadDocx(
            "<w:p><m:oMathPara><m:oMath><m:rad><m:radPr><m:degHide m:val=\"1\"/></m:radPr><m:deg/><m:e><m:f><m:num><m:r><m:t>a</m:t></m:r></m:num><m:den><m:r><m:t>b</m:t></m:r></m:den></m:f></m:e></m:rad></m:oMath></m:oMathPara></w:p>");
        PageLayout page = document.Layout(Isolated).Pages[0];
        ObjectBox equation = All(page.Boxes).OfType<ParagraphBox>().First().Children.OfType<ObjectBox>().Single();
        using Raster<Rgba8> raster = page.Render(dpi: 72);

        int dark = 0;
        for (int y = (int)equation.Bounds.Y; y < (int)Math.Ceiling(equation.Bounds.Bottom); y++)
        {
            foreach (Rgba8 pixel in raster.Row(y)[(int)equation.Bounds.X..(int)Math.Ceiling(equation.Bounds.Right)])
            {
                dark += pixel.R < 128 ? 1 : 0;
            }
        }

        Assert.True(dark > 10, $"{dark} dark pixels");
    }
}
