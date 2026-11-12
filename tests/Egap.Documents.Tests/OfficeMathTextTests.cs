using System.Xml.Linq;

using Egap.Documents.OfficeMath;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Tests;

/// <summary>
/// Equations as text (Build Plan tasks 15 and 16): UnicodeMath for alternative text and plain text, MathML Core for HTML and
/// for a tagged PDF's Formula elements, which carry both.
/// </summary>
public sealed class OfficeMathTextTests
{
    private static MathNode Tree(string content) => Omml.Read(OfficeMathTests.Zone(content));

    private static string Run(string text, string? style = null) =>
        style is null ? $"<m:r><m:t>{text}</m:t></m:r>" : $"<m:r><m:rPr><m:sty m:val=\"{style}\"/></m:rPr><m:t>{text}</m:t></m:r>";

    public static TheoryData<string, string> LinearForms() => new()
    {
        { $"<m:f><m:num>{Run("1")}</m:num><m:den>{Run("2")}</m:den></m:f>", "1/2" },
        { $"<m:f><m:num>{Run("a+b")}</m:num><m:den>{Run("c")}</m:den></m:f>", "(a+b)/c" },
        { $"<m:f><m:fPr><m:type m:val=\"lin\"/></m:fPr><m:num>{Run("1")}</m:num><m:den>{Run("2")}</m:den></m:f>", "1&#x2215;2" },
        { $"<m:sSup><m:e>{Run("x")}</m:e><m:sup>{Run("2")}</m:sup></m:sSup>", "x^2" },
        { $"<m:sSub><m:e>{Run("x")}</m:e><m:sub>{Run("i+1")}</m:sub></m:sSub>", "x_(i+1)" },
        { $"<m:sSubSup><m:e>{Run("x")}</m:e><m:sub>{Run("i")}</m:sub><m:sup>{Run("2")}</m:sup></m:sSubSup>", "x_i^2" },
        { $"<m:rad><m:radPr><m:degHide m:val=\"1\"/></m:radPr><m:deg/><m:e>{Run("x+1")}</m:e></m:rad>", "&#x221A;(x+1)" },
        { $"<m:rad><m:deg>{Run("3")}</m:deg><m:e>{Run("x")}</m:e></m:rad>", "&#x221A;(3&amp;x)" },
        { $"<m:nary><m:naryPr><m:chr m:val=\"&#x2211;\"/></m:naryPr><m:sub>{Run("i=1")}</m:sub><m:sup>{Run("n")}</m:sup><m:e>{Run("i")}</m:e></m:nary>", "&#x2211;_(i=1)^n&#x2592;i" },
        { $"<m:d><m:e>{Run("x")}</m:e><m:e>{Run("y")}</m:e></m:d>", "(x&#x2502;y)" },
        { $"<m:func><m:fName>{Run("sin", "p")}</m:fName><m:e>{Run("x")}</m:e></m:func>", "sin&#x2061;x" },
        { $"<m:acc><m:e>{Run("a")}</m:e></m:acc>", "a&#x302;" },
        { $"<m:limLow><m:e>{Run("lim", "p")}</m:e><m:lim>{Run("n&#x2192;&#x221E;")}</m:lim></m:limLow>", "lim&#x252C;(n&#x2192;&#x221E;)" },
        { $"<m:m><m:mr><m:e>{Run("1")}</m:e><m:e>{Run("0")}</m:e></m:mr><m:mr><m:e>{Run("0")}</m:e><m:e>{Run("1")}</m:e></m:mr></m:m>", "&#x25A0;(1&amp;0@0&amp;1)" },
        { Run("a-b"), "a&#x2212;b" },
    };

    [Theory]
    [MemberData(nameof(LinearForms))]
    public void EquationsWriteAsUnicodeMath(string content, string expected)
    {
        // The expected text is written with XML character references, as the markup is.
        string text = XElement.Parse($"<x>{expected}</x>").Value;
        Assert.Equal(text, UnicodeMath.Write(Tree(content)));
    }

    [Fact]
    public void DisplayParagraphsWriteOneEquationPerLine()
    {
        MathNode paragraph = Omml.Read(XElement.Parse(
            """<m:oMathPara xmlns:m="http://schemas.openxmlformats.org/officeDocument/2006/math"><m:oMath><m:r><m:t>a=b</m:t></m:r></m:oMath><m:oMath><m:r><m:t>c=d</m:t></m:r></m:oMath></m:oMathPara>"""));

        Assert.Equal("a=b\nc=d", UnicodeMath.Write(paragraph));
    }

    [Fact]
    public void EquationsWriteAsMathMLCore()
    {
        XNamespace m = MathMLWriter.Ns;
        XElement math = MathMLWriter.Write(Tree(
            $"<m:f><m:num>{Run("x")}</m:num><m:den>{Run("2")}</m:den></m:f>{Run("+")}<m:func><m:fName>{Run("sin", "p")}</m:fName><m:e>{Run("y")}</m:e></m:func>"));

        Assert.Equal(m + "math", math.Name);
        XElement row = Assert.Single(math.Elements());
        XElement fraction = row.Elements().First();
        Assert.Equal(m + "mfrac", fraction.Name);
        Assert.Equal(["mi", "mn"], fraction.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("+", row.Elements(m + "mo").First().Value);
        Assert.Contains(row.Descendants(m + "mi"), e => e.Value == "sin");
    }

    [Fact]
    public void EveryObjectWritesBothForms()
    {
        foreach (XElement zone in OfficeMathTests.EquationZones())
        {
            MathNode tree = Omml.Read(zone);
            Assert.NotEmpty(UnicodeMath.Write(tree));
            Assert.Equal(MathMLWriter.Ns + "math", MathMLWriter.Write(tree).Name);
        }
    }

    [Fact]
    public void AnEquationsTextIsItsUnicodeMath()
    {
        Document document = OfficeMathTests.LoadDocx(
            "<w:p><m:oMath><m:f><m:num><m:r><m:t>a+b</m:t></m:r></m:num><m:den><m:r><m:t>2</m:t></m:r></m:den></m:f></m:oMath></w:p>");

        Assert.Equal("(a+b)/2", Assert.Single(document.Descendants<Island>()).Text);
    }

    [Fact]
    public void TaggedPdfsMarkEquationsAsFormulas()
    {
        Document document = OfficeMathTests.LoadDocx(
            "<w:p><w:r><w:t>Area: </w:t></w:r><m:oMath><m:sSup><m:e><m:r><m:t>r</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup></m:oMath></w:p>");
        using PdfDocument pdf = document.ToPdf(new PdfExportOptions { Tagged = true, Layout = new Layout.LayoutOptions { Fonts = Layout.FontSettings.Portable } });

        PdfStructureElement formula = Assert.Single(pdf.Structure.Root!.DescendantsAndSelf(), e => e.Type == PdfStructureType.Formula);
        Assert.Equal("r^2", formula.AlternateText);
        PdfArray files = formula.Dictionary.Get<PdfArray>("AF")!;
        PdfDictionary spec = (PdfDictionary)files[0].Resolve()!;
        Assert.Equal("Supplement", spec.GetName("AFRelationship")!.Value);
        PdfStream file = (PdfStream)spec.Get<PdfDictionary>("EF")!.Get("F")!.Resolve()!;
        XElement math = XElement.Parse(System.Text.Encoding.UTF8.GetString(file.GetDecodedBytes()));
        Assert.Equal(MathMLWriter.Ns + "math", math.Name);
    }
}
