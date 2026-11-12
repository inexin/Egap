using System.Xml.Linq;

using Egami.Fonts;
using Egami.Fonts.Substitutes;

using Egap.Documents.Layout;
using Egap.Documents.OfficeMath;

namespace Egap.Documents.Tests;

/// <summary>
/// Line breaks in display equations (Build Plan task 15): manual breaks (<c>m:brk</c>), automatic breaks at operators when an
/// equation is wider than its line, and the document's <c>m:brkBin</c>, <c>m:brkBinSub</c>, <c>m:wrapIndent</c> and
/// <c>m:wrapRight</c> settings.
/// </summary>
public sealed class OfficeMathBreakTests
{
    private static readonly Typeface Stix = SubstituteFonts.Find("Cambria Math", FontStyle.Normal)!;

    private static string Run(string text) => $"<m:r><m:t>{text}</m:t></m:r>";

    private static string BreakRun(string text, int alignAt = 0) =>
        $"<m:r><m:rPr><m:brk{(alignAt > 0 ? $" m:alnAt=\"{alignAt}\"" : "")}/></m:rPr><m:t>{text}</m:t></m:r>";

    private static MathBox Display(string content, string? mathPr = null, float maxWidth = float.PositiveInfinity)
    {
        MathSettings settings = mathPr is null
            ? MathSettings.Default
            : MathSettings.Read(XElement.Parse($"<m:mathPr xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\">{mathPr}</m:mathPr>"));
        MathLayout layout = new(Stix, Stix.Math!, settings, 10);
        return layout.Zone((MathZone)Omml.Read(OfficeMathTests.Zone(content)), display: true, maxWidth);
    }

    /// <summary>The lines of a broken equation: its direct children, top to bottom.</summary>
    private static List<(MathBox Box, float X, float Y)> Lines(MathBox box) =>
        [.. box.Items.OfType<MathItem.Child>().Select(c => (c.Box, c.X, c.Y)).OrderBy(c => c.Y)];

    private static int Count(MathBox box, int c)
    {
        ushort glyph = Stix.GetGlyph(c);
        return box.Descendants().Sum(d => d.Box.Items.OfType<MathItem.Glyphs>().Sum(g => g.Ids.Count(id => id == glyph)))
            + box.Items.OfType<MathItem.Glyphs>().Sum(g => g.Ids.Count(id => id == glyph));
    }

    [Fact]
    public void AManualBreakStartsAnIndentedLine()
    {
        MathBox one = Display(Run("a=b+c"));
        MathBox two = Display(Run("a=b") + BreakRun("+c"));

        List<(MathBox Box, float X, float Y)> lines = Lines(two);
        Assert.Equal(2, lines.Count);
        Assert.Equal(0, lines[0].X);
        Assert.Equal(72, lines[1].X, 0.01f); // m:wrapIndent defaults to 1440 twips
        Assert.True(lines[1].Y > one.Height, $"{lines[1].Y} {one.Height}");
    }

    [Fact]
    public void ABreakCanAlignUnderAnOperatorOfTheFirstLine()
    {
        MathBox box = Display(Run("f=a+b") + BreakRun("+c", alignAt: 1));
        List<(MathBox Box, float X, float Y)> lines = Lines(box);
        MathBox first = Display(Run("f"));

        Assert.Equal(2, lines.Count);
        Assert.InRange(lines[1].X, first.Width, first.Width + 3); // under the "=", after the f and its spacing
    }

    [Fact]
    public void WideEquationsBreakAtOperatorsToFit()
    {
        string content = Run(string.Join("+", Enumerable.Range(0, 30).Select(i => "x")));
        MathBox whole = Display(content);
        MathBox broken = Display(content, maxWidth: whole.Width / 2.5f);

        List<(MathBox Box, float X, float Y)> lines = Lines(broken);
        Assert.InRange(lines.Count, 3, 4);
        Assert.True(lines[0].Box.Width <= whole.Width / 2.5f + 0.01f);
        Assert.All(lines.Skip(1), l => Assert.True(l.X + l.Box.Width <= (whole.Width / 2.5f) + 0.01f || l.X == 72));

        // An equation that fits never breaks.
        Assert.Equal(whole.Width, Display(content, maxWidth: whole.Width + 1).Width, 0.01f);
    }

    [Theory]
    [InlineData("before", 1)]
    [InlineData("after", 1)]
    [InlineData("repeat", 2)]
    public void BinaryOperatorsBreakPerBrkBin(string rule, int plusSigns)
    {
        MathBox manual = Display(Run("aaaaaaaa+bbbbbbbb"), $"<m:brkBin m:val=\"{rule}\"/>", maxWidth: 1);
        List<(MathBox Box, float X, float Y)> lines = Lines(manual);

        Assert.Equal(2, lines.Count);
        Assert.Equal(plusSigns, Count(manual, '+'));
        bool firstEndsWithPlus = Count(lines[0].Box, '+') == 1;
        Assert.Equal(rule != "before", firstEndsWithPlus);
    }

    [Theory]
    [InlineData("--", 2, 0)]
    [InlineData("-+", 1, 1)]
    [InlineData("+-", 1, 1)]
    public void ARepeatedMinusFollowsBrkBinSub(string sub, int minusSigns, int plusSigns)
    {
        MathBox box = Display(Run("aaaaaaaa-bbbbbbbb"), $"<m:brkBin m:val=\"repeat\"/><m:brkBinSub m:val=\"{sub}\"/>", maxWidth: 1);

        Assert.Equal(2, Lines(box).Count);
        Assert.Equal(minusSigns, Count(box, 0x2212));
        Assert.Equal(plusSigns, Count(box, '+'));
    }

    [Fact]
    public void RelationsStartTheNextLineAndWrapRightAlignsRight()
    {
        MathBox box = Display(Run("aaaaaaaa=bbbbbbbb"), "<m:wrapRight m:val=\"1\"/>", maxWidth: 1);
        List<(MathBox Box, float X, float Y)> lines = Lines(box);

        Assert.Equal(2, lines.Count);
        Assert.Equal(0, Count(lines[0].Box, '='));
        Assert.Equal(1, Count(lines[1].Box, '='));
        Assert.Equal(box.Width, lines[1].X + lines[1].Box.Width, 0.01f);
    }

    [Fact]
    public void LongDisplayEquationsBreakInsideTheirParagraph()
    {
        string terms = string.Join("+", Enumerable.Range(0, 120).Select(i => "x"));
        Document document = OfficeMathTests.LoadDocx($"<w:p><m:oMathPara><m:oMath>{Run(terms)}</m:oMath></m:oMathPara></w:p>");
        PageLayout page = document.Layout(new LayoutOptions { Fonts = FontSettings.Portable }).Pages[0];
        ParagraphBox paragraph = page.Boxes.SelectMany(b => (IEnumerable<LayoutBox>)[b, .. b.Children]).OfType<ParagraphBox>().First();
        ObjectBox equation = paragraph.Children.OfType<ObjectBox>().Single();

        Assert.True(equation.Bounds.Width <= paragraph.Bounds.Width + 0.01, $"{equation.Bounds} in {paragraph.Bounds}");
        Assert.True(equation.Bounds.Height > 30, $"{equation.Bounds}");
    }
}
