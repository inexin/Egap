using System.Xml.Linq;

using Egap.Documents.OfficeMath;

namespace Egap.Documents.Tests;

/// <summary>The MathML reader (Build Plan task 17): MathML Core from ODT formulas and HTML pages into the math tree.</summary>
public sealed class MathMLReaderTests
{
    private static MathNode Read(string content, string attributes = "") =>
        MathMLReader.Read(XElement.Parse($"<math xmlns=\"http://www.w3.org/1998/Math/MathML\"{attributes}>{content}</math>"));

    private static string Linear(string content) => UnicodeMath.Write(Read(content));

    private static MathObject Single(string content) =>
        Assert.IsType<MathObject>(Assert.Single(Assert.IsType<MathZone>(Read(content)).Content.Items));

    [Fact]
    public void TokensBecomeRunsAndIdentifiersKeepTheirStyle()
    {
        Assert.Equal("x+1", Linear("<mi>x</mi><mo>+</mo><mn>1</mn>"));
        MathZone zone = Assert.IsType<MathZone>(Read("<mi>sin</mi><mi mathvariant=\"normal\">d</mi><mi mathvariant=\"bold\">v</mi><mtext>if</mtext>"));
        MathRun[] runs = [.. zone.Content.Items.OfType<MathRun>()];
        Assert.Equal("p", runs[0].Style);
        Assert.Equal("p", runs[1].Style);
        Assert.Equal("b", runs[2].Style);
        Assert.True(runs[3].Normal);
    }

    [Theory]
    [InlineData("<mfrac><mi>a</mi><mi>b</mi></mfrac>", "Fraction")]
    [InlineData("<msqrt><mi>x</mi></msqrt>", "Radical")]
    [InlineData("<mroot><mi>x</mi><mn>3</mn></mroot>", "Radical")]
    [InlineData("<msub><mi>x</mi><mn>1</mn></msub>", "Subscript")]
    [InlineData("<msup><mi>x</mi><mn>2</mn></msup>", "Superscript")]
    [InlineData("<msubsup><mi>x</mi><mn>1</mn><mn>2</mn></msubsup>", "SubSuperscript")]
    [InlineData("<mover><mi>x</mi><mo>^</mo></mover>", "Accent")]
    [InlineData("<mover><mi>x</mi><mo>\u203E</mo></mover>", "Bar")]
    [InlineData("<munder><mrow><mi>a</mi><mo>+</mo><mi>b</mi></mrow><mo>\u23DF</mo></munder>", "GroupCharacter")]
    [InlineData("<munder><mi>lim</mi><mrow><mi>n</mi><mo>\u2192</mo><mi>\u221E</mi></mrow></munder>", "LowerLimit")]
    [InlineData("<mrow><mo>(</mo><mi>a</mi><mo separator=\"true\">,</mo><mi>b</mi><mo>)</mo></mrow>", "Delimiter")]
    [InlineData("<mfenced><mi>a</mi><mi>b</mi></mfenced>", "Delimiter")]
    [InlineData("<mtable><mtr><mtd><mi>a</mi></mtd><mtd><mi>b</mi></mtd></mtr></mtable>", "Matrix")]
    [InlineData("<mtable><mtr><mtd><mi>a</mi></mtd></mtr><mtr><mtd><mi>b</mi></mtd></mtr></mtable>", "EquationArray")]
    [InlineData("<mphantom><mi>x</mi></mphantom>", "Phantom")]
    [InlineData("<menclose><mi>x</mi></menclose>", "BorderBox")]
    [InlineData("<mmultiscripts><mi>C</mi><mprescripts/><mn>1</mn><mn>2</mn></mmultiscripts>", "PreSubSuperscript")]
    public void ElementsBecomeTheirOfficeMathObjects(string content, string kind)
    {
        Assert.Equal(kind, Single(content).Kind.ToString());
    }

    [Fact]
    public void FencesAndSeparatorsSplitADelimitersArguments()
    {
        MathObject delimiter = Single("<mrow><mo>[</mo><mi>a</mi><mo separator=\"true\">,</mo><mi>b</mi><mo>)</mo></mrow>");
        Assert.Equal("[", delimiter.Properties!.Get("begChr"));
        Assert.Equal(")", delimiter.Properties.Get("endChr"));
        Assert.Equal(2, delimiter.All("e").Count());

        // Fences that do not pair stay runs.
        MathZone zone = Assert.IsType<MathZone>(Read("<mrow><mo>(</mo><mi>a</mi><mo>)</mo><mo>+</mo><mo>(</mo><mi>b</mi><mo>)</mo></mrow>"));
        Assert.Equal(2, zone.Content.Items.OfType<MathObject>().Count());
    }

    [Fact]
    public void NaryOperatorsTakeTheirScriptsAndOperand()
    {
        MathZone zone = Assert.IsType<MathZone>(Read(
            "<munderover><mo>\u2211</mo><mrow><mi>i</mi><mo>=</mo><mn>1</mn></mrow><mi>n</mi></munderover><msup><mi>i</mi><mn>2</mn></msup><mo>=</mo><mi>S</mi>"));
        MathObject nary = Assert.IsType<MathObject>(zone.Content.Items[0]);
        Assert.Equal(MathKind.Nary, nary.Kind);
        Assert.Equal("\u2211", nary.Properties!.Get("chr"));
        Assert.Equal("undOvr", nary.Properties.Get("limLoc"));
        Assert.Equal(MathKind.Superscript, Assert.IsType<MathObject>(Assert.Single(nary.Argument("e").Items)).Kind);
        Assert.Equal(3, zone.Content.Items.Count); // the sum, "=" and "S"

        MathObject integral = Assert.IsType<MathObject>(Assert.IsType<MathZone>(Read("<msubsup><mo>\u222B</mo><mn>0</mn><mn>1</mn></msubsup><mi>x</mi>")).Content.Items[0]);
        Assert.Equal("subSup", integral.Properties!.Get("limLoc"));
    }

    [Fact]
    public void AFunctionApplicationJoinsItsNameAndArgument()
    {
        MathObject function = Single("<mi>sin</mi><mo>\u2061</mo><mi>x</mi>");
        Assert.Equal(MathKind.Function, function.Kind);
        Assert.Equal("sin", Assert.IsType<MathRun>(Assert.Single(function.Argument("fName").Items)).Text);
    }

    [Fact]
    public void DisplayMathIsAParagraph()
    {
        Assert.IsType<MathParagraph>(Read("<mi>x</mi>", " display=\"block\""));
        Assert.IsType<MathParagraph>(Read("<mi>x</mi>", " mode=\"display\""));
        Assert.IsType<MathZone>(Read("<mi>x</mi>"));
    }

    [Fact]
    public void WhatTheWriterWritesReadsBack()
    {
        string[] samples =
        [
            "<m:f><m:num><m:r><m:t>a+b</m:t></m:r></m:num><m:den><m:r><m:t>2</m:t></m:r></m:den></m:f>",
            "<m:sSup><m:e><m:r><m:t>x</m:t></m:r></m:e><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSup><m:r><m:t>+1</m:t></m:r>",
            "<m:rad><m:radPr><m:degHide m:val=\"1\"/></m:radPr><m:deg/><m:e><m:r><m:t>y</m:t></m:r></m:e></m:rad>",
            "<m:d><m:e><m:r><m:t>a</m:t></m:r></m:e><m:e><m:r><m:t>b</m:t></m:r></m:e></m:d>",
            "<m:nary><m:naryPr><m:chr m:val=\"\u2211\"/></m:naryPr><m:sub><m:r><m:t>i=1</m:t></m:r></m:sub><m:sup><m:r><m:t>n</m:t></m:r></m:sup><m:e><m:r><m:t>i</m:t></m:r></m:e></m:nary>",
        ];
        foreach (string sample in samples)
        {
            MathNode omml = Omml.Read(OfficeMathTests.Zone(sample));
            MathNode back = MathMLReader.Read(MathMLWriter.Write(omml));
            Assert.Equal(UnicodeMath.Write(omml), UnicodeMath.Write(back));
        }
    }

    [Fact]
    public void DeepOrOddMarkupReadsWithoutThrowing()
    {
        string deep = string.Concat(Enumerable.Repeat("<mrow><mfrac>", 500)) + "<mi>x</mi><mi>y</mi>" + string.Concat(Enumerable.Repeat("</mfrac><mi>z</mi></mrow>", 500));
        Assert.NotNull(Read(deep));
        Assert.NotNull(Read("<mfrac/><msub/><mroot/><mmultiscripts/><mmultiscripts><mprescripts/><mi>a</mi></mmultiscripts><mtable><mtr/></mtable><munderover><mo>\u2211</mo></munderover><unknown><mi>x</mi></unknown>"));
    }
}
