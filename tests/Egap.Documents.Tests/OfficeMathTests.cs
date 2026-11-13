using System.Xml.Linq;

using Egami.Fonts;

using Egap.Documents.Docx;
using Egap.Documents.Layout;
using Egap.Documents.OfficeMath;
using Egap.Documents.Rtf;

namespace Egap.Documents.Tests;

/// <summary>
/// The math tree (Build Plan tasks 10 and 11): OMML read and written back unchanged for every object, RTF math read onto the
/// same tree, the document's math properties, and the math font chain.
/// </summary>
public sealed class OfficeMathTests
{
    private const string MNs = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    private const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>One equation per OMML object, with properties, written from ECMA-376 Part 1 §22.1.</summary>
    private static readonly string[] EquationList =
    [
        """<m:acc><m:accPr><m:chr m:val="&#x302;"/><m:ctrlPr><w:rPr><w:i/></w:rPr></m:ctrlPr></m:accPr><m:e><m:r><m:t>a</m:t></m:r></m:e></m:acc>""",
        """<m:bar><m:barPr><m:pos m:val="top"/></m:barPr><m:e><m:r><m:t>b</m:t></m:r></m:e></m:bar>""",
        """<m:box><m:boxPr><m:opEmu m:val="1"/><m:noBreak m:val="0"/><m:diff/><m:brk m:alnAt="1"/><m:aln/></m:boxPr><m:e><m:r><m:t>==</m:t></m:r></m:e></m:box>""",
        """<m:borderBox><m:borderBoxPr><m:hideTop m:val="on"/><m:strikeBLTR/></m:borderBoxPr><m:e><m:r><m:t>c</m:t></m:r></m:e></m:borderBox>""",
        """<m:d><m:dPr><m:begChr m:val="["/><m:sepChr m:val="|"/><m:endChr m:val=""/><m:grow m:val="0"/><m:shp m:val="match"/></m:dPr><m:e><m:r><m:t>x</m:t></m:r></m:e><m:e><m:r><m:t>y</m:t></m:r></m:e></m:d>""",
        """<m:eqArr><m:eqArrPr><m:baseJc m:val="top"/><m:maxDist m:val="1"/><m:rSpRule m:val="3"/><m:rSp m:val="240"/></m:eqArrPr><m:e><m:r><m:t>x&amp;=1</m:t></m:r></m:e><m:e><m:r><m:t>y&amp;=2</m:t></m:r></m:e></m:eqArr>""",
        """<m:f><m:fPr><m:type m:val="skw"/></m:fPr><m:num><m:r><m:t>1</m:t></m:r></m:num><m:den><m:r><m:t>2</m:t></m:r></m:den></m:f>""",
        """<m:func><m:funcPr/><m:fName><m:r><m:rPr><m:sty m:val="p"/></m:rPr><m:t>sin</m:t></m:r></m:fName><m:e><m:r><m:t>x</m:t></m:r></m:e></m:func>""",
        """<m:groupChr><m:groupChrPr><m:chr m:val="&#x23DF;"/><m:pos m:val="bot"/><m:vertJc m:val="top"/></m:groupChrPr><m:e><m:r><m:t>a+b</m:t></m:r></m:e></m:groupChr>""",
        """<m:limLow><m:e><m:r><m:rPr><m:sty m:val="p"/></m:rPr><m:t>lim</m:t></m:r></m:e><m:lim><m:r><m:t>n&#x2192;&#x221E;</m:t></m:r></m:lim></m:limLow>""",
        """<m:limUpp><m:limUppPr/><m:e><m:r><m:t>x</m:t></m:r></m:e><m:lim><m:r><m:t>2</m:t></m:r></m:lim></m:limUpp>""",
        """<m:m><m:mPr><m:baseJc m:val="bottom"/><m:plcHide m:val="1"/><m:cSp m:val="300"/><m:mcs><m:mc><m:mcPr><m:count m:val="2"/><m:mcJc m:val="left"/></m:mcPr></m:mc></m:mcs></m:mPr><m:mr><m:e><m:r><m:t>1</m:t></m:r></m:e><m:e/></m:mr><m:mr><m:e/><m:e><m:r><m:t>1</m:t></m:r></m:e></m:mr></m:m>""",
        """<m:nary><m:naryPr><m:chr m:val="&#x2211;"/><m:limLoc m:val="undOvr"/><m:grow m:val="1"/><m:subHide m:val="0"/></m:naryPr><m:sub><m:r><m:t>i=1</m:t></m:r></m:sub><m:sup><m:r><m:t>n</m:t></m:r></m:sup><m:e><m:r><m:t>i</m:t></m:r></m:e></m:nary>""",
        """<m:phant><m:phantPr><m:show m:val="0"/><m:zeroWid/><m:transp m:val="1"/></m:phantPr><m:e><m:r><m:t>q</m:t></m:r></m:e></m:phant>""",
        """<m:rad><m:radPr><m:degHide m:val="1"/></m:radPr><m:deg/><m:e><m:r><m:t>2</m:t></m:r></m:e></m:rad>""",
        """<m:sPre><m:sub><m:r><m:t>1</m:t></m:r></m:sub><m:sup><m:r><m:t>2</m:t></m:r></m:sup><m:e><m:r><m:t>X</m:t></m:r></m:e></m:sPre>""",
        """<m:sSub><m:e><m:r><m:t>x</m:t></m:r></m:e><m:sub><m:r><m:t>i</m:t></m:r></m:sub></m:sSub>""",
        """<m:sSup><m:e><m:argPr><m:argSz m:val="-1"/></m:argPr><m:r><m:t>y</m:t></m:r></m:e><m:sup><m:r><m:t>3</m:t></m:r></m:sup></m:sSup>""",
        """<m:sSubSup><m:sSubSupPr><m:alnScr m:val="1"/></m:sSubSupPr><m:e><m:r><m:t>x</m:t></m:r></m:e><m:sub><m:r><m:t>i</m:t></m:r></m:sub><m:sup><m:r><m:t>2</m:t></m:r></m:sup></m:sSubSup>""",
        """<m:r><m:rPr><m:scr m:val="double-struck"/><m:sty m:val="b"/></m:rPr><w:rPr><w:rFonts w:ascii="Cambria Math" w:hAnsi="Cambria Math"/></w:rPr><m:t xml:space="preserve"> ab </m:t><w:br/><m:t>c</m:t></m:r><m:r><m:rPr><m:nor/><m:lit/><m:brk m:alnAt="2"/><m:aln/></m:rPr><m:t>text</m:t></m:r>""",
        """<m:sSup><m:e><m:f><m:num><m:rad><m:deg/><m:e><m:d><m:e><m:r><m:t>x</m:t></m:r></m:e></m:d></m:e></m:rad></m:num><m:den><m:r><m:t>2</m:t></m:r></m:den></m:f></m:e><m:sup><m:r><m:t>n</m:t></m:r></m:sup></m:sSup>""",
        """<w:bookmarkStart w:id="0" w:name="eq"/><m:r><m:t>a</m:t></m:r><w:bookmarkEnd w:id="0"/><m:unknown m:val="1"><m:r><m:t>z</m:t></m:r></m:unknown>""",
    ];

    public static TheoryData<string> Equations() => [.. EquationList];

    private static XElement Zone(string content) => XElement.Parse($"""<m:oMath xmlns:m="{MNs}" xmlns:w="{WNs}">{content}</m:oMath>""");

    /// <summary>The element without namespace declarations, which only say where prefixes are declared.</summary>
    private static XElement Bare(XElement element)
    {
        XElement copy = new(element);
        copy.DescendantsAndSelf().Attributes().Where(a => a.IsNamespaceDeclaration).ToList().ForEach(a => a.Remove());
        return copy;
    }

    private static void AssertSame(XElement expected, XElement actual) =>
        Assert.True(XNode.DeepEquals(Bare(expected), Bare(actual)), $"expected\n{Bare(expected)}\nactual\n{Bare(actual)}");

    [Theory]
    [MemberData(nameof(Equations))]
    public void EveryObjectIsWrittenBackAsRead(string content)
    {
        XElement zone = Zone(content);

        AssertSame(zone, Omml.Write(Omml.Read(zone)));
    }

    [Fact]
    public void ObjectsReadIntoTheirKindsAndArguments()
    {
        MathZone zone = Assert.IsType<MathZone>(Omml.Read(Zone(EquationList[^2])));
        MathObject sup = Assert.IsType<MathObject>(Assert.Single(zone.Content.Items));
        Assert.Equal(MathKind.Superscript, sup.Kind);
        MathObject fraction = Assert.IsType<MathObject>(Assert.Single(sup.Argument("e").Items));
        Assert.Equal(MathKind.Fraction, fraction.Kind);
        MathObject radical = Assert.IsType<MathObject>(Assert.Single(fraction.Argument("num").Items));
        Assert.Equal(MathKind.Radical, radical.Kind);
        Assert.Empty(radical.Argument("deg").Items);
        Assert.Equal("n", Assert.IsType<MathRun>(Assert.Single(sup.Argument("sup").Items)).Text);

        MathObject matrix = Assert.IsType<MathObject>(Assert.Single(Assert.IsType<MathZone>(Omml.Read(Zone(EquationList[11]))).Content.Items));
        Assert.Equal(MathKind.Matrix, matrix.Kind);
        Assert.Equal([2, 2], matrix.Rows.Select(r => r.Count));
        Assert.Equal("300", matrix.Properties!.Get("cSp"));
        Assert.True(matrix.Properties.On("plcHide"));
        Assert.NotNull(matrix.Properties.Element("mcs"));

        MathObject delimiter = Assert.IsType<MathObject>(Assert.Single(Assert.IsType<MathZone>(Omml.Read(Zone(EquationList[4]))).Content.Items));
        Assert.Equal("[", delimiter.Properties!.Char("begChr", "("));
        Assert.Equal("", delimiter.Properties.Char("endChr", ")"));
        Assert.Equal(2, delimiter.All("e").Count());
    }

    [Fact]
    public void RunPropertiesAreRead()
    {
        MathZone zone = Assert.IsType<MathZone>(Omml.Read(Zone(EquationList[^3])));
        MathRun first = Assert.IsType<MathRun>(zone.Content.Items[0]);
        MathRun second = Assert.IsType<MathRun>(zone.Content.Items[1]);

        Assert.Equal(("double-struck", "b"), (first.Script, first.Style));
        Assert.Equal(" ab c", first.Text);
        Assert.Single(first.WordProperties);
        Assert.True(second.Normal && second.Literal);
        Assert.False(first.Normal);
    }

    [Fact]
    public void ADisplayParagraphKeepsItsJustificationAndEquations()
    {
        XElement para = XElement.Parse($"""<m:oMathPara xmlns:m="{MNs}"><m:oMathParaPr><m:jc m:val="left"/></m:oMathParaPr><m:oMath><m:r><m:t>a=b</m:t></m:r></m:oMath><m:oMath><m:r><m:t>c=d</m:t></m:r></m:oMath></m:oMathPara>""");

        MathParagraph paragraph = Assert.IsType<MathParagraph>(Omml.Read(para));
        Assert.Equal("left", paragraph.Justification);
        Assert.Equal(2, paragraph.Items.OfType<MathZone>().Count());
        AssertSame(para, Omml.Write(paragraph));
    }

    [Fact]
    public void NestingDeeperThanTheLimitIsKeptAsXml()
    {
        string deep = string.Concat(Enumerable.Repeat("<m:sSup><m:e>", 500)) + "<m:r><m:t>x</m:t></m:r>" + string.Concat(Enumerable.Repeat("</m:e><m:sup/></m:sSup>", 500));
        XElement zone = Zone(deep);

        AssertSame(zone, Omml.Write(Omml.Read(zone)));
    }

    private static Document LoadDocx(string paragraphs, string? mathPr = null)
    {
        OpcPackageWriter writer = new();
        writer.AddXmlPart("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
            XDocument.Parse($"""<w:document xmlns:w="{WNs}" xmlns:m="{MNs}"><w:body>{paragraphs}</w:body></w:document>"""));
        writer.AddRelationship("/", "rId1", OpcRelationshipTypes.OfficeDocument, "word/document.xml");
        if (mathPr is not null)
        {
            writer.AddXmlPart("/word/settings.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml",
                XDocument.Parse($"""<w:settings xmlns:w="{WNs}" xmlns:m="{MNs}">{mathPr}</w:settings>"""));
            writer.AddRelationship("/word/document.xml", "rId2", OpcRelationshipTypes.Settings, "settings.xml");
        }

        using MemoryStream stream = new();
        writer.Save(stream);
        stream.Position = 0;
        return DocxFormat.Load(stream);
    }

    [Fact]
    public void DocxEquationsReadIntoTreesThatWriteBackTheirMarkup()
    {
        string paragraphs = string.Concat(EquationList.Select(e => $"<w:p><w:r><w:t>x</w:t></w:r><m:oMath>{e}</m:oMath></w:p>"))
            + $"""<w:p><m:oMathPara><m:oMath><m:r><m:t>a</m:t></m:r></m:oMath></m:oMathPara></w:p>""";
        Document document = LoadDocx(paragraphs);
        Island[] islands = [.. document.Descendants<Island>().Where(i => i.Kind == IslandKind.Math)];

        Assert.Equal(EquationList.Length + 1, islands.Length);
        foreach (Island island in islands)
        {
            MathNode tree = MathIslands.Tree(island)!;
            Assert.Same(tree, MathIslands.Tree(island));
            AssertSame(XElement.Parse(island.Markup), Omml.Write(tree));
        }

        Assert.IsType<MathParagraph>(MathIslands.Tree(islands[^1]));
    }

    [Fact]
    public void BrokenMarkupHasNoTree()
    {
        Document document = new();
        Assert.Null(MathIslands.Tree(new Island(document, IslandKind.Math, DocxExtension.Format, "<m:oMath")));
        Assert.Null(MathIslands.Tree(new Island(document, IslandKind.Math, "odt", "<math/>")));
        Assert.Null(MathIslands.Tree(new Island(document, IslandKind.Other, DocxExtension.Format, $"""<m:oMath xmlns:m="{MNs}"/>""")));
    }

    /// <summary>RTF math in Word's spelling and the OMML it reads as.</summary>
    public static TheoryData<string, string> RtfEquations() => new()
    {
        {
            @"{\mmath{\*\moMath{\rtlch\ltrch\i{\mr\mscr0\msty2 x}}}}",
            """<m:r><m:rPr><m:scr m:val="roman"/><m:sty m:val="i"/></m:rPr><m:t>x</m:t></m:r>"""
        },
        {
            @"{\mmath{\*\moMath{\mf{\mfPr{\mtype skw}{\mctrlPr}}{\mnum{\i{\mr\msty2 1}}}{\mden{{\mr\msty2 2}}}}}}",
            """<m:f><m:fPr><m:type m:val="skw"/><m:ctrlPr/></m:fPr><m:num><m:r><m:rPr><m:sty m:val="i"/></m:rPr><m:t>1</m:t></m:r></m:num><m:den><m:r><m:rPr><m:sty m:val="i"/></m:rPr><m:t>2</m:t></m:r></m:den></m:f>"""
        },
        {
            @"{\mmath{\*\moMath{\mnary{\mnaryPr{\mchr " + "\\u8721 " + @"?}{\mlimLoc undOvr}{\mgrow on}}{\msub{\mr i=1}}{\msup{\mr n}}{\me{\mr i}}}}}",
            """<m:nary><m:naryPr><m:chr m:val="&#x2211;"/><m:limLoc m:val="undOvr"/><m:grow m:val="on"/></m:naryPr><m:sub><m:r><m:t>i=1</m:t></m:r></m:sub><m:sup><m:r><m:t>n</m:t></m:r></m:sup><m:e><m:r><m:t>i</m:t></m:r></m:e></m:nary>"""
        },
        {
            @"{\mmath{\*\moMath{\md{\mdPr{\mbegChr [}{\mendChr \'7d}{\mshp match}}{\me{\mr x}}{\me{\mr y}}}}}",
            """<m:d><m:dPr><m:begChr m:val="["/><m:endChr m:val="}"/><m:shp m:val="match"/></m:dPr><m:e><m:r><m:t>x</m:t></m:r></m:e><m:e><m:r><m:t>y</m:t></m:r></m:e></m:d>"""
        },
        {
            @"{\mmath{\*\moMath{\mm{\mmPr{\mbaseJc bot}{\mmcs{\mmc{\mmcPr{\mcount 2}{\mmcJc left}}}}}{\mmr{\me{\mr 1}}{\me{\i 0}}}}}}",
            """<m:m><m:mPr><m:baseJc m:val="bottom"/><m:mcs><m:mc><m:mcPr><m:count m:val="2"/><m:mcJc m:val="left"/></m:mcPr></m:mc></m:mcs></m:mPr><m:mr><m:e><m:r><m:t>1</m:t></m:r></m:e><m:e><m:r><m:t>0</m:t></m:r></m:e></m:mr></m:m>"""
        },
        {
            @"{\mmath{\*\moMath{\b{\mr\mscr3\msty1 ab}}{{\mr\mnor text}}{{\mr\mscr2\msty3\mlit +-}}{{\mr\mscr5\msty0 \\\{\}}}}}",
            """<m:r><m:rPr><m:scr m:val="double-struck"/><m:sty m:val="b"/></m:rPr><m:t>ab</m:t></m:r><m:r><m:rPr><m:nor/></m:rPr><m:t>text</m:t></m:r><m:r><m:rPr><m:scr m:val="fraktur"/><m:sty m:val="bi"/><m:lit/></m:rPr><m:t>+-</m:t></m:r><m:r><m:rPr><m:scr m:val="monospace"/><m:sty m:val="p"/></m:rPr><m:t>\{}</m:t></m:r>"""
        },
        {
            @"{\mmath{\*\moMath{\mrad{\mradPr{\mdegHide on}}{\mdeg}{\me{\mr 2}}}{\mmathPict{\*\shppict{\pict\pngblip 89504e47}}}}}",
            """<m:rad><m:radPr><m:degHide m:val="on"/></m:radPr><m:deg/><m:e><m:r><m:t>2</m:t></m:r></m:e></m:rad>"""
        },
    };

    [Theory]
    [MemberData(nameof(RtfEquations))]
    public void RtfMathReadsAsTheOmmlItSpells(string rtf, string omml)
    {
        AssertSame(Zone(omml), RtfMath.ToOmml(rtf)!);
    }

    [Fact]
    public void RtfDisplayParagraphsAndIslandsReadOntoTheTree()
    {
        Document document = RtfFormat.Parse(@"{\rtf1\ansi{\fonttbl{\f0 Times New Roman;}{\f1 Cambria Math;}}{\mmathPr\mmathFont1\mbrkBin2\mbrkBinSub2\mdefJc3\mwrapIndent720\mintLim1\mnaryLim0\msmallFrac1}"
            + @"x {\mmath{\*\moMathPara{\moMathParaPr\mjc4}{\*\moMath{\mr a}}{\*\moMath{\mr b}}}}\par}");

        Island island = document.Descendants<Island>().Single();
        MathParagraph paragraph = Assert.IsType<MathParagraph>(MathIslands.Tree(island));
        Assert.Equal("right", paragraph.Justification);
        Assert.Equal(["a", "b"], paragraph.Items.OfType<MathZone>().Select(z => Assert.IsType<MathRun>(Assert.Single(z.Content.Items)).Text));

        MathSettings settings = document.MathSettings;
        Assert.Equal(("Cambria Math", "repeat", "-+", "left"), (settings.Font, settings.BreakBinary, settings.BreakBinarySubtraction, settings.DefaultJustification));
        Assert.Equal((720, "undOvr", "subSup", true), (settings.WrapIndent, settings.IntegralLimits, settings.NaryLimits, settings.SmallFractions));
    }

    [Fact]
    public void RtfNestingAndJunkDoNotThrow()
    {
        string deep = @"{\mmath{\*\moMath" + string.Concat(Enumerable.Repeat(@"{\msSup{\me", 2000)) + @"{\mr x}" + new string('}', 4000) + "}}";
        Assert.NotNull(RtfMath.ToOmml(deep));
        Assert.Null(RtfMath.ToOmml(@"{\mmath plain text \u-3 \'ff}"));
        Assert.NotNull(RtfMath.ToOmml(@"{\mmath{\*\moMath{\mf{\mnum")); // cut short: what was read
        Assert.Null(RtfMath.ToOmml(""));
    }

    [Fact]
    public void DocxMathPropertiesAndTheirDefaults()
    {
        MathSettings defaults = LoadDocx("<w:p/>").MathSettings;
        Assert.Equal(("Cambria Math", "before", "--", "centerGroup"), (defaults.Font, defaults.BreakBinary, defaults.BreakBinarySubtraction, defaults.DefaultJustification));
        Assert.Equal((1440, "subSup", "undOvr", true, false), (defaults.WrapIndent, defaults.IntegralLimits, defaults.NaryLimits, defaults.DisplayDefaults, defaults.SmallFractions));

        MathSettings read = LoadDocx("<w:p/>", """<m:mathPr><m:mathFont m:val="STIX Two Math"/><m:brkBin m:val="after"/><m:dispDef m:val="0"/><m:lMargin m:val="120"/><m:rMargin m:val="240"/><m:preSp m:val="10"/><m:postSp m:val="20"/><m:interSp m:val="30"/><m:intraSp m:val="40"/><m:wrapRight/></m:mathPr>""").MathSettings;
        Assert.Equal(("STIX Two Math", "after", false), (read.Font, read.BreakBinary, read.DisplayDefaults));
        Assert.Equal((120, 240, 10, 20, 30, 40, true), (read.LeftMargin, read.RightMargin, read.SpaceBefore, read.SpaceAfter, read.SpaceBetween, read.SpaceWithin, read.WrapRight));
        Assert.Same(read.Properties, read.Properties);
    }

    [Fact]
    public void WithoutCambriaMathEquationsUseStixTwoMath()
    {
        Document document = new();
        FontProvider fonts = new(document, new FontSettings { UseSystemFonts = false });

        (Typeface face, TypefaceMath math) = MathFonts.Resolve(fonts, document.MathSettings);

        Assert.Equal("STIX Two Math", face.FamilyName);
        Assert.InRange(math.Constants.AxisHeight, 200, 300);
        ushort paren = face.GetGlyph('(');
        Assert.True(math.GetVariants(paren, MathDirection.Vertical).Count > 3);
        Assert.NotNull(math.GetAssembly(paren, MathDirection.Vertical));
        Assert.Contains(fonts.Substitutions, s => s.Requested == "Cambria Math");
    }

    [Fact]
    public void AMathFontWithoutAMathTableFallsBack()
    {
        Document document = new() { MathSettings = MathSettings.Read(XElement.Parse($"""<m:mathPr xmlns:m="{MNs}"><m:mathFont m:val="Arial"/></m:mathPr>""")) };
        FontProvider fonts = new(document, new FontSettings { UseSystemFonts = false });

        (Typeface face, _) = MathFonts.Resolve(fonts, document.MathSettings);

        Assert.Equal("STIX Two Math", face.FamilyName);
    }

    [Fact]
    public void AnInstalledCambriaMathIsUsedWithItsMathTable()
    {
        if (!FontCollection.System.TryGetFamily("Cambria Math", out _))
        {
            return; // Cambria Math ships with Windows and Office; elsewhere STIX Two Math stands in (test above).
        }

        Document document = new();
        (Typeface face, TypefaceMath math) = MathFonts.Resolve(new FontProvider(document), document.MathSettings);

        Assert.Equal("Cambria Math", face.FamilyName);
        Assert.InRange(math.Constants.ScriptPercentScaleDown, 60, 85);
        ushort brace = face.GetGlyph('{');
        Assert.True(math.GetVariants(brace, MathDirection.Vertical).Count > 3);
        Assert.Contains(math.GetAssembly(brace, MathDirection.Vertical)!.Parts, p => p.IsExtender);
    }
}
