using System.Text;

namespace Egap.Pdf.Tests.Content;

/// <summary>The graphics state walker: Phase 3 task 3.</summary>
public sealed class ContentWalkerTests
{
    [Fact]
    public void TracksTheMatrixThroughSavesAndRestores()
    {
        Recorder walker = Walk("q 2 0 0 2 10 20 cm 0 0 1 1 re f Q 0 0 1 1 re f Q");

        Assert.Equal(2, walker.Paths.Count);
        Assert.Equal(new PdfMatrix(2, 0, 0, 2, 10, 20), walker.Paths[0].Matrix);
        Assert.Equal(new PdfRectangle(10, 20, 12, 22), walker.Paths[0].Path.GetBounds(walker.Paths[0].Matrix));
        Assert.Equal(PdfMatrix.Identity, walker.Paths[1].Matrix);
        Assert.Equal(PdfPathPaint.Fill, walker.Paths[1].Paint);
    }

    [Fact]
    public void BuildsPathsWithEveryOperator()
    {
        Recorder walker = Walk("10 10 m 20 10 l 30 30 40 40 50 10 c 60 60 70 70 v 80 80 90 90 y h 0 0 5 5 re W* n");

        Painted painted = Assert.Single(walker.Paths);
        PdfPath path = painted.Path;
        Assert.Equal(PdfPathPaint.EvenOddClip, painted.Paint);
        Assert.Equal(1, painted.Clips);
        Assert.Equal(
            [PdfPathVerb.MoveTo, PdfPathVerb.LineTo, PdfPathVerb.CurveTo, PdfPathVerb.CurveTo, PdfPathVerb.CurveTo, PdfPathVerb.Close, PdfPathVerb.MoveTo, PdfPathVerb.LineTo, PdfPathVerb.LineTo, PdfPathVerb.LineTo, PdfPathVerb.Close],
            path.Segments.Select(s => s.Verb));
        Assert.Equal(new PdfPoint(50, 10), path.Segments[3].Control1);
        Assert.Equal(new PdfPoint(90, 90), path.Segments[4].Control2);
    }

    [Fact]
    public void TracksColoursAndColourSpaces()
    {
        PdfDictionary resources = new()
        {
            ["ColorSpace"] = new PdfDictionary
            {
                ["CS0"] = new PdfArray { PdfName.Get("Separation"), PdfName.Get("Spot"), PdfName.Get("DeviceCMYK"), new PdfDictionary() },
            },
        };
        Recorder walker = Walk("0.5 g 1 0 0 RG 0 0 m S /CS0 cs 0 0 m f 0.25 sc 0 0 m f 0 0 0 1 K /Pattern cs /P1 scn 0 0 m B", resources);

        Assert.Equal("DeviceGray [0.5]", walker.Paths[0].Fill);
        Assert.Equal("DeviceRGB [1 0 0]", walker.Paths[0].Stroke);
        Assert.Equal("Separation [1]", walker.Paths[1].Fill);
        Assert.Equal("Separation [0.25]", walker.Paths[2].Fill);
        Assert.Equal("Pattern [] /P1", walker.Paths[3].Fill);
        Assert.Equal("DeviceCMYK [0 0 0 1]", walker.Paths[3].Stroke);
    }

    [Fact]
    public void AppliesGraphicsStateDictionaries()
    {
        PdfDictionary resources = new()
        {
            ["ExtGState"] = new PdfDictionary
            {
                ["GS0"] = new PdfDictionary
                {
                    ["LW"] = new PdfReal(2.5),
                    ["CA"] = new PdfReal(0.5),
                    ["ca"] = new PdfReal(0.25),
                    ["BM"] = new PdfArray { PdfName.Get("Bogus"), PdfName.Get("Multiply") },
                    ["D"] = new PdfArray { new PdfArray { new PdfInteger(3), new PdfInteger(1) }, new PdfInteger(2) },
                    ["SMask"] = new PdfDictionary { ["S"] = PdfName.Get("Luminosity") },
                },
            },
        };
        Recorder walker = Walk("/GS0 gs 0 0 m S", resources);

        PdfGraphicsState state = Assert.Single(walker.States);
        Assert.Equal(2.5, state.LineWidth);
        Assert.Equal((0.5, 0.25), (state.StrokeAlpha, state.FillAlpha));
        Assert.Equal("Multiply", state.BlendMode.Value);
        Assert.Equal([3.0, 1.0], state.DashArray);
        Assert.Equal(2, state.DashPhase);
        Assert.NotNull(state.SoftMask);
        Assert.Empty(walker.Warnings);
    }

    [Fact]
    public void ShowsGlyphsAndMovesTheTextMatrix()
    {
        PdfDictionary resources = Fonts(("F1", new PdfDictionary
        {
            ["Type"] = PdfName.Get("Font"),
            ["Subtype"] = PdfName.Get("Type1"),
            ["BaseFont"] = PdfName.Get("Helvetica"),
            ["FirstChar"] = new PdfInteger(32),
            ["Widths"] = new PdfArray { new PdfInteger(250), new PdfInteger(300), new PdfInteger(400) },
        }));
        Recorder walker = Walk("BT /F1 10 Tf 2 Tc 5 Tw 50 Tz 100 200 Td ( !\") Tj [(!) -1000 (!)] TJ 12 TL T* (!) ' ET", resources);

        Assert.Equal([32, 33, 34, 33, 33, 33], walker.Glyphs.Select(g => g.Code));
        Assert.True(walker.Glyphs[0].IsWordSpace);

        // Each advance is (width / 1000 * size + Tc + Tw for a space) * Tz.
        Assert.Equal(new PdfMatrix(5, 0, 0, 10, 100, 200), walker.Glyphs[0].Matrix);
        Assert.Equal(100 + ((2.5 + 2 + 5) * 0.5), walker.Glyphs[1].Matrix.E, 9);
        Assert.Equal(100 + ((2.5 + 2 + 5 + 3 + 2) * 0.5), walker.Glyphs[2].Matrix.E, 9);

        // TJ moves back by -(-1000) / 1000 * size * Tz: 5 units to the right.
        double afterFirst = walker.Glyphs[3].Matrix.E + ((3 + 2) * 0.5);
        Assert.Equal(afterFirst + 5, walker.Glyphs[4].Matrix.E, 9);
        Assert.Equal((100, 176), (walker.Glyphs[5].Matrix.E, walker.Glyphs[5].Matrix.F));
    }

    [Fact]
    public void UsesStandardWidthsWhenAFontHasNone()
    {
        PdfDictionary resources = Fonts(("F1", new PdfDictionary { ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Courier") }));
        Recorder walker = Walk("BT /F1 10 Tf (ab) Tj ET", resources);

        Assert.Equal(6, walker.Glyphs[1].Matrix.E, 9);
    }

    [Fact]
    public void SplitsCompositeFontStringsIntoTwoByteCodes()
    {
        PdfDictionary resources = Fonts(("F1", new PdfDictionary
        {
            ["Subtype"] = PdfName.Get("Type0"),
            ["Encoding"] = PdfName.Get("Identity-H"),
            ["DescendantFonts"] = new PdfArray
            {
                new PdfDictionary
                {
                    ["DW"] = new PdfInteger(500),
                    ["W"] = new PdfArray { new PdfInteger(3), new PdfArray { new PdfInteger(600), new PdfInteger(700) }, new PdfInteger(10), new PdfInteger(20), new PdfInteger(900) },
                },
            },
        }));
        Recorder walker = Walk("BT /F1 10 Tf <00030004000F0063> Tj ET", resources);

        Assert.Equal([3, 4, 15, 99], walker.Glyphs.Select(g => g.Code));
        Assert.Equal([600.0, 700, 900, 500], walker.Glyphs.Select(g => g.Width));
        Assert.Equal(6 + 7 + 9, walker.Glyphs[3].Matrix.E, 9);
    }

    [Fact]
    public void EntersFormsWithTheirMatrixAndClip()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfStream form = Stream("0 0 1 1 re f /Fm0 Do", new PdfDictionary
        {
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(10), new PdfInteger(10) },
            ["Matrix"] = new PdfArray { new PdfInteger(1), new PdfInteger(0), new PdfInteger(0), new PdfInteger(1), new PdfInteger(100), new PdfInteger(0) },
        });
        PdfReference reference = pdf.Objects.Add(form);
        PdfDictionary resources = new() { ["XObject"] = new PdfDictionary { ["Fm0"] = reference } };
        form.Dictionary["Resources"] = resources;
        Recorder walker = Walk("/Fm0 Do 0 0 1 1 re f", resources);

        Assert.Equal(2, walker.Paths.Count);
        Assert.Equal(PdfMatrix.Translation(100, 0), walker.Paths[0].Matrix);
        Assert.Equal(1, walker.Paths[0].Clips);
        Assert.Equal(PdfMatrix.Identity, walker.Paths[1].Matrix);
        Assert.Equal(0, walker.Paths[1].Clips);
        Assert.Equal(1, walker.Forms);
        Assert.Contains(walker.Warnings, w => w.Message.Contains("draws itself", StringComparison.Ordinal));
    }

    [Fact]
    public void WalksType3GlyphProcedures()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfDictionary resources = Fonts(("T3", new PdfDictionary
        {
            ["Subtype"] = PdfName.Get("Type3"),
            ["FontMatrix"] = new PdfArray { new PdfReal(0.01), new PdfInteger(0), new PdfInteger(0), new PdfReal(0.01), new PdfInteger(0), new PdfInteger(0) },
            ["FirstChar"] = new PdfInteger(65),
            ["Widths"] = new PdfArray { new PdfInteger(50) },
            ["Encoding"] = new PdfDictionary { ["Differences"] = new PdfArray { new PdfInteger(65), PdfName.Get("square") } },
            ["CharProcs"] = new PdfDictionary { ["square"] = pdf.Objects.Add(Stream("50 0 d0 0 0 100 100 re f")) },
        }));
        Recorder walker = Walk("BT /T3 12 Tf 10 20 Td (AA) Tj ET", resources);

        Assert.Equal(2, walker.Paths.Count);
        Assert.Equal(new PdfRectangle(10, 20, 22, 32), walker.Paths[0].Path.GetBounds(walker.Paths[0].Matrix));
        Assert.Equal(new PdfRectangle(16, 20, 28, 32), walker.Paths[1].Path.GetBounds(walker.Paths[1].Matrix));
    }

    [Fact]
    public void TracksMarkedContent()
    {
        PdfDictionary layer = new() { ["Type"] = PdfName.Get("OCG"), ["Name"] = PdfString.FromText("Notes") };
        PdfDictionary resources = new() { ["Properties"] = new PdfDictionary { ["MC0"] = layer } };
        Recorder walker = Walk("/OC /MC0 BDC /Span << /MCID 7 /ActualText (fi) >> BDC 0 0 m f EMC EMC EMC /Tag MP", resources);

        Assert.Equal(2, walker.MarkedAtPaint[0].Count);
        Assert.Same(layer, walker.MarkedAtPaint[0][0].OptionalContent);
        Assert.Equal(7, walker.MarkedAtPaint[0][1].Mcid);
        Assert.Equal("fi", walker.MarkedAtPaint[0][1].ActualText);
        Assert.Equal(["OC", "Span", "/Span", "/OC", "Tag"], walker.MarkedEvents);
        Assert.Single(walker.Warnings);
    }

    [Fact]
    public void IgnoresDamageAndKeepsGoing()
    {
        Recorder walker = Walk("Q Q 1 0 0 cm (x) Tj /Nope Do /Nope gs /F9 9 Tf foo 0 0 m f");

        Assert.Single(walker.Paths);
        Assert.Equal(4, walker.Warnings.Count);
    }

    [Fact]
    public void WalksAPageWithItsResources()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        page.Dictionary["Contents"] = pdf.Objects.Add(Stream("/F1 1 Tf BT (a) Tj ET"));
        page.Resources = Fonts(("F1", new PdfDictionary { ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Helvetica") }));
        Recorder walker = new();

        walker.Walk(page);

        Assert.Single(walker.Glyphs);
        Assert.Empty(walker.Warnings);
    }

    private static PdfDictionary Fonts(params (string Name, PdfDictionary Font)[] fonts)
    {
        PdfDictionary dictionary = [];
        foreach ((string name, PdfDictionary font) in fonts)
        {
            dictionary[name] = font;
        }

        return new PdfDictionary { ["Font"] = dictionary };
    }

    private static PdfStream Stream(string content, PdfDictionary? dictionary = null)
    {
        PdfStream stream = new(dictionary ?? []);
        stream.SetDecodedBytes(Encoding.ASCII.GetBytes(content));
        return stream;
    }

    private static Recorder Walk(string content, PdfDictionary? resources = null)
    {
        Recorder walker = new();
        walker.Walk(PdfContent.Parse(Encoding.ASCII.GetBytes(content)), resources, PdfMatrix.Identity);
        return walker;
    }

    private sealed class Recorder : PdfContentWalker
    {
        public List<Painted> Paths { get; } = [];

        public List<PdfGraphicsState> States { get; } = [];

        public List<PdfGlyph> Glyphs { get; } = [];

        public List<IReadOnlyList<PdfMarkedContent>> MarkedAtPaint { get; } = [];

        public List<string> MarkedEvents { get; } = [];

        public int Forms { get; private set; }

        protected override void OnPaintPath(PdfPath path, PdfPathPaint paint)
        {
            Paths.Add(new Painted(path, paint, State.Matrix, State.Clips.Count, State.FillColor.ToString(), State.StrokeColor.ToString()));
            States.Add(State);
            MarkedAtPaint.Add([.. MarkedContent]);
        }

        protected override void OnGlyph(PdfGlyph glyph)
        {
            Glyphs.Add(glyph);
        }

        protected override bool OnBeginForm(PdfStream form, PdfName name)
        {
            Forms++;
            return true;
        }

        protected override void OnBeginMarkedContent(PdfMarkedContent markedContent)
        {
            MarkedEvents.Add(markedContent.Tag.Value);
        }

        protected override void OnEndMarkedContent(PdfMarkedContent markedContent)
        {
            MarkedEvents.Add("/" + markedContent.Tag.Value);
        }

        protected override void OnMarkedPoint(PdfMarkedContent point)
        {
            MarkedEvents.Add(point.Tag.Value);
        }
    }
}

/// <summary>A path as the walker reported it, with the state's matrix, clip count and colours.</summary>
internal sealed record Painted(PdfPath Path, PdfPathPaint Paint, PdfMatrix Matrix, int Clips, string Fill, string Stroke);
