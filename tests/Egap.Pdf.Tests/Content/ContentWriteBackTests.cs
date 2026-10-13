using System.Text;

namespace Egap.Pdf.Tests.Content;

/// <summary>The public content writer and writing edited content back to pages: Phase 3 task 4.</summary>
public sealed class ContentWriteBackTests
{
    [Fact]
    public void EveryWriterOperatorReadsBack()
    {
        PdfInlineImage image = new(new PdfDictionary { ["W"] = new PdfInteger(1), ["H"] = new PdfInteger(1), ["CS"] = PdfName.Get("G"), ["BPC"] = new PdfInteger(8) }, [0x7F]);
        byte[] bytes = new PdfContentWriter()
            .Save().Transform(PdfMatrix.Translation(1, 2)).LineWidth(0.000012345).LineCap(1).LineJoin(2).MiterLimit(4).Dash([2, 1], 0.5)
            .RenderingIntent("Perceptual").Flatness(1).GraphicsState("GS0")
            .Gray(0.5).Rgb(1, 0, 0, stroke: true).Cmyk(0, 0, 0, 1).ColorSpace("Pattern").Color([], "P1").ColorSpace("CS0", stroke: true).Color([0.25], stroke: true)
            .MoveTo(0, 0).LineTo(1, 1).CurveTo(1, 2, 3, 4, 5, 6).CurveToV(1, 1, 2, 2).CurveToY(3, 3, 4, 4).ClosePath().Rectangle(0, 0, 10, 10)
            .Clip(evenOdd: true).EndPath().Stroke().CloseAndStroke().Fill().Fill(evenOdd: true).FillAndStroke().FillAndStroke(evenOdd: true)
            .CloseFillAndStroke().CloseFillAndStroke(evenOdd: true).Shading("Sh0").Draw("Im0").InlineImage(image)
            .BeginMarkedContent("Span", new PdfDictionary { ["MCID"] = new PdfInteger(1) }).BeginMarkedContent("Artifact").EndMarkedContent().EndMarkedContent()
            .MarkedPoint("Tag").MarkedPoint("Tag", PdfName.Get("MC0"))
            .BeginText().Font("F1", 12).MoveText(1, 2).MoveTextSetLeading(0, -14).TextMatrix(PdfMatrix.Identity).CharacterSpacing(0.5).WordSpacing(1)
            .HorizontalScaling(90).Leading(14).TextRenderMode(2).TextRise(-3).NextLine().ShowText("ab"u8).ShowText([("A"u8.ToArray(), -50), ("B"u8.ToArray(), 0)], hex: true)
            .NextLineShowText("c"u8).NextLineShowText(1, 2, "d"u8).EndText()
            .GlyphWidth(500).GlyphWidthAndBox(500, 0, 0, 0, 400, 700).Compatibility(true).Operation(new PdfOperation("xyz")).Compatibility(false)
            .Restore()
            .ToArray();

        PdfContent content = PdfContent.Parse(bytes);

        Assert.Empty(content.Warnings);
        Assert.Equal(
            ["q", "cm", "w", "J", "j", "M", "d", "ri", "i", "gs", "g", "RG", "k", "cs", "scn", "CS", "SCN", "m", "l", "c", "v", "y", "h", "re",
             "W*", "n", "S", "s", "f", "f*", "B", "B*", "b", "b*", "sh", "Do", "BI", "BDC", "BMC", "EMC", "EMC", "MP", "DP",
             "BT", "Tf", "Td", "TD", "Tm", "Tc", "Tw", "Tz", "TL", "Tr", "Ts", "T*", "Tj", "TJ", "'", "\"", "ET", "d0", "d1", "BX", "xyz", "EX", "Q"],
            content.Select(o => o.Operator));
        Assert.Equal(0.0000123450, content[2].GetNumber(0));
        Assert.Equal([0x7F], ((PdfInlineImage)content[36]).Data.ToArray());
        Assert.Contains("\n.5 g\n", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void UnchangedContentChangesNothing()
    {
        (byte[] file, _) = ThreeStreams();
        using PdfDocument pdf = PdfDocument.Open(file);
        PdfPage page = pdf.Pages[0];

        page.SetContent(page.GetContent());

        Assert.False(pdf.IsChanged);
        using MemoryStream saved = new();
        pdf.Save(saved);
        Assert.Equal(file, saved.ToArray());
    }

    [Fact]
    public void KeepsTheStreamsThatDidNotChange()
    {
        (byte[] file, _) = ThreeStreams();
        using PdfDocument pdf = PdfDocument.Open(file);
        PdfPage page = pdf.Pages[0];
        PdfArray before = page.Dictionary.Get<PdfArray>("Contents")!.Clone();
        PdfContent content = page.GetContent();

        content.Insert(content.IndexOf(content.First(o => o.Operator == "f")), new PdfOperation("w", new PdfInteger(3)));
        content.Insert(0, new PdfOperation("q"));
        content.Add(new PdfOperation("Q"));
        page.SetContent(content);

        PdfArray after = page.Dictionary.Get<PdfArray>("Contents")!;
        Assert.Equal(5, after.Count);
        Assert.Same(before[0], after[1]);
        Assert.NotSame(before[1], after[2]);
        Assert.Same(before[2], after[3]);
        Assert.Equal("q\n", Decoded(after[0]));
        Assert.Equal("\n0 0 10 10 re\n3 w\nf\n", Decoded(after[2]));
        Assert.Equal("\nQ\n", Decoded(after[4]));

        using MemoryStream saved = new();
        pdf.Save(saved);
        using PdfDocument reopened = PdfDocument.Open(saved.ToArray());
        Assert.Equal(content.Select(o => o.ToString()), reopened.Pages[0].GetContent().Select(o => o.ToString()));
        Assert.Equal(file, saved.ToArray()[..file.Length]);
    }

    [Fact]
    public void WritesNewStreamsUncompressedWhenThePagesWere()
    {
        (byte[] file, _) = ThreeStreams(compress: false);
        using PdfDocument pdf = PdfDocument.Open(file);
        PdfPage page = pdf.Pages[0];
        PdfContent content = page.GetContent();
        content.Add(new PdfOperation("n"));

        page.SetContent(content);

        PdfStream added = (PdfStream)page.Dictionary.Get<PdfArray>("Contents")![^1].Resolve();
        Assert.Empty(added.Filters);
        Assert.Equal("\nn\n", Encoding.ASCII.GetString(added.GetDecodedBytes()));
    }

    [Fact]
    public void ReplacesContentFromElsewhereWithOneStream()
    {
        (byte[] file, _) = ThreeStreams();
        using PdfDocument pdf = PdfDocument.Open(file);
        PdfPage page = pdf.Pages[0];

        page.SetContent(new PdfContent([new PdfOperation("q"), new PdfOperation("Q")]));

        PdfStream stream = Assert.IsType<PdfStream>(page.Dictionary.Get("Contents")!.Resolve());
        Assert.Equal("FlateDecode", stream.Filters.Single().Value);
        Assert.Equal("q\nQ\n", Encoding.ASCII.GetString(stream.GetDecodedBytes()));

        page.SetContent([]);
        Assert.Null(page.Dictionary.Get("Contents"));
    }

    [Fact]
    public void RewritesStreamsThatShareAnOperationAsOne()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        page.Dictionary["Contents"] = new PdfArray { pdf.Objects.Add(Stream("0 0 10")), pdf.Objects.Add(Stream("10 re f")) };
        PdfContent content = page.GetContent();
        content.Add(new PdfOperation("n"));

        page.SetContent(content);

        PdfStream stream = Assert.IsType<PdfStream>(page.Dictionary.Get("Contents")!.Resolve());
        Assert.Equal("0 0 10 10 re\nf\nn\n", Encoding.ASCII.GetString(stream.GetDecodedBytes()));
    }

    [Fact]
    public void KeepsASingleStreamAndStartsNewOnesOnANewLine()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        PdfReference original = pdf.Objects.Add(Stream("0 0 10 10 re f % ends in a comment"));
        page.Dictionary["Contents"] = original;
        PdfContent content = page.GetContent();
        content.Add(new PdfOperation("n"));

        page.SetContent(content);

        PdfArray contents = page.Dictionary.Get<PdfArray>("Contents")!;
        Assert.Same(original, contents[0]);
        Assert.Equal("\nn\n", Decoded(contents[1]));
        Assert.Equal(["re", "f", "n"], page.GetContent().Select(o => o.Operator));
    }

    [Fact]
    public void RewritesContentThatEndsInsideAString()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        page.Dictionary["Contents"] = pdf.Objects.Add(Stream("BT (open string ET"));
        PdfContent content = page.GetContent();
        content.Add(new PdfOperation("ET"));

        page.SetContent(content);

        Assert.IsType<PdfStream>(page.Dictionary.Get("Contents"));
        Assert.Equal(["BT", "ET"], page.GetContent().Select(o => o.Operator).Where(o => o != "Tj").Take(1).Append("ET"));
    }

    private static string Decoded(PdfObject entry)
    {
        return Encoding.ASCII.GetString(((PdfStream)entry.Resolve()).GetDecodedBytes());
    }

    private static PdfStream Stream(string content, bool compress = true)
    {
        PdfStream stream = new();
        stream.SetDecodedBytes(Encoding.ASCII.GetBytes(content), compress);
        return stream;
    }

    /// <summary>A saved file whose one page has three content streams: a save and matrix, a filled square, and a restore.</summary>
    private static (byte[] File, PdfArray Contents) ThreeStreams(bool compress = true)
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        PdfArray contents = [pdf.Objects.Add(Stream("q 1 0 0 1 0 0 cm", compress)), pdf.Objects.Add(Stream("0 0 10 10 re f", compress)), pdf.Objects.Add(Stream("Q", compress))];
        page.Dictionary["Contents"] = contents;
        using MemoryStream output = new();
        pdf.Save(output);
        return (output.ToArray(), contents);
    }
}
