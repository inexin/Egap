using System.Text;

namespace Egap.Pdf.Tests.Content;

/// <summary>The content lexer and operation list: Phase 3 task 2.</summary>
public sealed class ContentParserTests
{
    [Fact]
    public void ReadsOperatorsWithTheirOperands()
    {
        PdfContent content = Parse("q 1 0 0 1 72.5 -720 cm /F1 12 Tf BT (Hi) Tj [(A) -120 (B)] TJ ET 0.5 g Q");

        Assert.Equal(["q", "cm", "Tf", "BT", "Tj", "TJ", "ET", "g", "Q"], content.Select(o => o.Operator));
        Assert.Empty(content.Warnings);
        Assert.Equal(new PdfMatrix(1, 0, 0, 1, 72.5, -720), content[1].GetMatrix());
        Assert.Equal("F1", content[2].GetName(0)!.Value);
        Assert.Equal(12, content[2].GetNumber(1));
        Assert.Equal("Hi", Encoding.ASCII.GetString(content[4].GetString(0)!.Bytes));
        PdfArray array = Assert.IsType<PdfArray>(Assert.Single(content[5].Operands));
        Assert.Equal(3, array.Count);
        Assert.Equal(-120, array.GetNumber(1));
        Assert.IsType<PdfReal>(content[7].Operands[0]);
        Assert.True(content.All(o => o.IsKnown));
    }

    [Fact]
    public void ReadsEveryKindOfOperand()
    {
        PdfContent content = Parse("""
            /OC /MC0 BDC /Span << /ActualText (x\(y\)\101\n) /MCID 3 /Nested << /A [1 [2 3]] >> >> BDC
            <48 65 6C 6C 6F> Tj <4> Tj /A#20B MP true false null 3 X EMC EMC
            % a comment
            -.5 g +3 g -0 g 1.25 g 4. g
            """);

        PdfDictionary span = Assert.IsType<PdfDictionary>(content[1].Operands[1]);
        Assert.Equal("x(y)A\n", Encoding.ASCII.GetString(span.Get<PdfString>("ActualText")!.Bytes));
        Assert.Equal(3, span.GetInt("MCID"));
        Assert.Equal(3, span.Get<PdfDictionary>("Nested")!.Get<PdfArray>("A")!.Get<PdfArray>(1)!.GetNumber(1));
        Assert.Equal("Hello", Encoding.ASCII.GetString(content[2].GetString(0)!.Bytes));
        Assert.True(content[2].GetString(0)!.IsHex);
        Assert.Equal([0x40], content[3].GetString(0)!.Bytes.ToArray());
        Assert.Equal("A B", content[4].GetName(0)!.Value);
        PdfOperation unknown = content[5];
        Assert.Equal("X", unknown.Operator);
        Assert.False(unknown.IsKnown);
        Assert.Equal([PdfBoolean.True, PdfBoolean.False, PdfNull.Instance, new PdfInteger(3)], unknown.Operands);
        Assert.Equal([-0.5, 3, 0, 1.25, 4], content.Skip(8).Select(o => o.GetNumber(0) ?? double.NaN));
        Assert.IsType<PdfReal>(content[12].Operands[0]);
        Assert.Empty(content.Warnings);
    }

    [Fact]
    public void ParsesNumbersAsTheirExactValues()
    {
        string[] numbers = ["0.1", "123.456", "-0.0001", "3.14159265358979", "12345678901234567890", "0.000000000000000001", "5-6", "--3", "-"];
        PdfContent content = Parse(string.Join(" ", numbers) + " X");

        PdfOperation operation = Assert.Single(content);
        Assert.Equal(0.1, operation.GetNumber(0));
        Assert.Equal(123.456, operation.GetNumber(1));
        Assert.Equal(-0.0001, operation.GetNumber(2));
        Assert.Equal(3.14159265358979, operation.GetNumber(3));
        Assert.Equal(12345678901234567890.0, operation.GetNumber(4));
        Assert.Equal(1e-18, operation.GetNumber(5));
        Assert.Equal(56, operation.GetNumber(6));
        Assert.Equal(-3, operation.GetNumber(7));
        Assert.Equal(0, operation.GetNumber(8));
        Assert.Equal(3, content.Warnings.Count);
    }

    [Fact]
    public void KeepsUnknownOperatorsAndWarnsOnWrongCounts()
    {
        PdfContent content = Parse("1 2 3 m 7 sh0 BX 1 2 foo EX");

        Assert.Equal(["m", "sh0", "BX", "foo", "EX"], content.Select(o => o.Operator));
        Assert.Equal(3, content[0].Operands.Count);
        Assert.Equal(PdfWarningCode.WrongOperandCount, Assert.Single(content.Warnings).Code);
    }

    [Fact]
    public void SkipsStrayBytesWithWarnings()
    {
        PdfContent content = Parse("q ) } > ] >> Q 1 2");

        Assert.Equal(["q", "Q"], content.Select(o => o.Operator));
        Assert.Equal(6, content.Warnings.Count);
        Assert.All(content.Warnings, w => Assert.Equal(PdfWarningCode.MalformedContent, w.Code));
    }

    [Fact]
    public void ClosesAnArrayLeftOpenAtTheNextOperator()
    {
        PdfContent content = Parse("[(A) 10 (B) TJ 0 g");

        Assert.Equal(["TJ", "g"], content.Select(o => o.Operator));
        Assert.Equal(3, Assert.IsType<PdfArray>(content[0].Operands[0]).Count);
        Assert.Single(content.Warnings);
    }

    [Fact]
    public void ThrowsInStrictMode()
    {
        Assert.Throws<PdfException>(() => PdfContent.Parse("q ) Q"u8, new PdfReadOptions { Strict = true }));
    }

    [Fact]
    public void StopsDeepNesting()
    {
        byte[] deep = Encoding.ASCII.GetBytes(new string('[', 1000) + " X");

        PdfLimitException e = Assert.Throws<PdfLimitException>(() => PdfContent.Parse(deep, new PdfReadOptions { MaxNestingDepth = 100 }));
        Assert.Equal(nameof(PdfReadOptions.MaxNestingDepth), e.Limit);
    }

    [Fact]
    public void ReadsInlineImagesByTheirLength()
    {
        // 4 by 2 RGB samples, 24 bytes, holding "EI" and white space inside the data.
        byte[] samples = [.. "EI EI \nEI"u8, .. Enumerable.Range(0, 15).Select(i => (byte)i)];
        byte[] data = [.. "q BI /W 4 /H 2 /BPC 8 /CS /RGB ID "u8, .. samples, .. "\nEI Q"u8];
        PdfContent content = PdfContent.Parse(data);

        Assert.Equal(["q", "BI", "Q"], content.Select(o => o.Operator));
        PdfInlineImage image = Assert.IsType<PdfInlineImage>(content[1]);
        Assert.Equal(samples, image.Data.ToArray());
        Assert.Equal(4, image.Parameters.GetInt("W"));
        Assert.Equal("RGB", image.Parameters.GetName("CS")!.Value);
        Assert.Empty(content.Warnings);
    }

    [Fact]
    public void FindsTheEndOfFilteredInlineImagesBySearching()
    {
        PdfContent content = Parse("BI /W 8 /H 8 /F /AHx /CS /G ID\n0123EIab456 >\nEI\nQ BI /W 1 /H 1 /F [/Fl] ID \u00ff\u0001EI\u0080 EI q");

        Assert.Equal(["BI", "Q", "BI", "q"], content.Select(o => o.Operator));
        Assert.Equal("0123EIab456 >", Encoding.Latin1.GetString(((PdfInlineImage)content[0]).Data));
        Assert.Equal("\u00ff\u0001EI\u0080", Encoding.Latin1.GetString(((PdfInlineImage)content[2]).Data));
    }

    [Fact]
    public void ReadsAnInlineImageWithNoEndToTheEnd()
    {
        PdfContent content = Parse("BI /W 1 /H 1 /F /DCT ID \u0001\u0002\u0003");

        Assert.Equal([1, 2, 3], ((PdfInlineImage)Assert.Single(content)).Data.ToArray());
        Assert.Equal(PdfWarningCode.MalformedInlineImage, Assert.Single(content.Warnings).Code);
    }

    [Fact]
    public void OperationsCheckWhatTheyAreGiven()
    {
        Assert.Throws<ArgumentException>(() => new PdfOperation("", []));
        Assert.Throws<ArgumentException>(() => new PdfOperation("a b", []));
        Assert.Throws<ArgumentException>(() => new PdfOperation("Do", [new PdfStream()]));
        Assert.Throws<ArgumentException>(() => new PdfOperation("Do", [new PdfArray { new PdfStream() }]));

        PdfOperation operation = new("cm", new PdfInteger(1), new PdfInteger(0), new PdfInteger(0), new PdfInteger(1), new PdfReal(10.5), new PdfInteger(-3));
        Assert.Equal("1 0 0 1 10.5 -3 cm", operation.ToString());
        Assert.Same(Parse("q")[0].Operator, new PdfOperation("q").Operator);
    }

    [Fact]
    public void TracksChanges()
    {
        PdfContent content = Parse("q Q");
        Assert.False(content.IsChanged);

        content.Insert(1, new PdfOperation("n"));

        Assert.True(content.IsChanged);
        Assert.Equal("q\nn\nQ\n", Encoding.ASCII.GetString(content.ToBytes()));
    }

    [Fact]
    public void ReadsAPagesContentArrayAsOneSequence()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        page.Dictionary["Contents"] = new PdfArray
        {
            pdf.Objects.Add(Stream("q 1 0 0 1 0 0")),
            pdf.Objects.Add(Stream("cm 0 g")),
            new PdfInteger(5),
            pdf.Objects.Add(Stream("Q")),
        };

        PdfContent content = page.GetContent();

        Assert.Equal(["q", "cm", "g", "Q"], content.Select(o => o.Operator));
        Assert.Equal(PdfWarningCode.MalformedContent, Assert.Single(content.Warnings).Code);
        Assert.True(content.Origin!.Joined);
        Assert.Empty(pdf.Pages.AddBlank().GetContent());
    }

    [Fact]
    public void RemembersWhichStreamEachOperationCameFrom()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        page.Dictionary["Contents"] = new PdfArray { pdf.Objects.Add(Stream("q")), pdf.Objects.Add(Stream("0 g 0 0 1 1 re f")), pdf.Objects.Add(Stream("Q")) };

        PdfContent content = page.GetContent();

        Assert.False(content.Origin!.Joined);
        Assert.Equal([1, 3, 1], content.Origin.Segments.Select(s => s.Operations.Length));
    }

    private static PdfStream Stream(string content)
    {
        PdfStream stream = new();
        stream.SetDecodedBytes(Encoding.ASCII.GetBytes(content));
        return stream;
    }

    private static PdfContent Parse(string content)
    {
        return PdfContent.Parse(Encoding.Latin1.GetBytes(content));
    }
}
