using System.Text;

using Egap.Pdf.IO;
using Egap.Pdf.Objects;
using Egap.Pdf.Parsing;

namespace Egap.Pdf.Tests.Parsing;

public sealed class ObjectParserTests
{
    private static ObjectParser Parser(string text, bool strict = false, PdfReadOptions? options = null, IObjectResolver? resolver = null)
    {
        return Parser(Encoding.Latin1.GetBytes(text), strict, options, resolver);
    }

    private static ObjectParser Parser(byte[] bytes, bool strict = false, PdfReadOptions? options = null, IObjectResolver? resolver = null)
    {
        Lexer lexer = new(new SourceReader(PdfSource.FromMemory(bytes)), new WarningLog(strict));
        return new ObjectParser(lexer, options ?? PdfReadOptions.Default, resolver);
    }

    private static string Write(PdfObject value)
    {
        return Encoding.Latin1.GetString(ObjectWriter.ToBytes(value));
    }

    private static PdfWarningCode[] Warnings(ObjectParser parser)
    {
        return [.. parser.Lexer.Log.ToArray().Select(w => w.Code)];
    }

    private sealed class Resolver(Dictionary<int, PdfObject> objects) : IObjectResolver
    {
        public PdfObject Resolve(PdfReference reference)
        {
            return objects.GetValueOrDefault(reference.ObjectNumber) ?? PdfNull.Instance;
        }
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("null", "null")]
    [InlineData("-17", "-17")]
    [InlineData("3.250", "3.250")]
    [InlineData("(text)", "(text)")]
    [InlineData("<0aFF>", "<0AFF>")]
    [InlineData("/Name", "/Name")]
    [InlineData("12 0 R", "12 0 R")]
    [InlineData("[1 2 0 R 3 /A [] << >>]", "[1 2 0 R 3 /A [] << >>]")]
    [InlineData("<</A 1/B<</C[(x)]>>/D 4 0 R>>", "<< /A 1 /B << /C [(x)] >> /D 4 0 R >>")]
    [InlineData("[1 2 3 R]", "[1 2 3 R]")]
    [InlineData("[1 2 R 4]", "[1 2 R 4]")]
    public void ParsesDirectObjects(string text, string expected)
    {
        ObjectParser parser = Parser(text, strict: true);

        Assert.Equal(expected, Write(parser.ParseObject()));
    }

    [Fact]
    public void ReferencesNeedAGenerationAndR()
    {
        ObjectParser parser = Parser("1 2 3");

        Assert.Equal(1, Assert.IsType<PdfInteger>(parser.ParseObject()).Value);
        Assert.Equal(2, Assert.IsType<PdfInteger>(parser.ParseObject()).Value);
        Assert.Equal(3, Assert.IsType<PdfInteger>(parser.ParseObject()).Value);
    }

    [Fact]
    public void ReferencesResolveThroughTheResolver()
    {
        Resolver resolver = new(new() { [5] = new PdfInteger(42) });
        PdfDictionary dictionary = Assert.IsType<PdfDictionary>(Parser("<< /X 5 0 R >>", resolver: resolver).ParseObject());

        Assert.Equal(42, dictionary.GetInt("X"));
        Assert.IsType<PdfReference>(dictionary["X"]);
    }

    [Fact]
    public void WrittenObjectsParseBackToTheSameBytes()
    {
        PdfDictionary value = new()
        {
            ["Name"] = PdfName.Get("with space#and(parens)"),
            ["Text"] = PdfString.FromText("é€ (x) \\ \r\n end"),
            ["Utf16"] = PdfString.FromText("Ω"),
            ["Hex"] = PdfString.FromBytes([0, 1, 254, 255], hex: true),
            ["Numbers"] = new PdfArray([0, -1, long.MaxValue, long.MinValue, 0.5, -1e-9, 1e20, 3.0]),
            ["Ref"] = new PdfReference(7, 3),
            ["Nested"] = new PdfArray([new PdfArray([new PdfDictionary { ["Deep"] = PdfNull.Instance }])]),
        };
        string written = Write(value);

        ObjectParser parser = Parser(written, strict: true);
        PdfObject parsed = parser.ParseObject();

        Assert.Equal(written, Write(parsed));
        Assert.Equal("é€ (x) \\ \r\n end", ((PdfDictionary)parsed).GetText("Text"));
    }

    [Fact]
    public void ParsesIndirectObjects()
    {
        ObjectParser parser = Parser("% junk\n12 3 obj\n<< /Type /X >>\nendobj\n13 0 obj 5 endobj", strict: true);

        IndirectObject first = parser.ParseIndirectObject(7);
        Assert.Equal(12, first.ObjectNumber);
        Assert.Equal(3, first.Generation);
        Assert.Equal("<< /Type /X >>", Write(first.Value));

        IndirectObject second = parser.ParseIndirectObject(parser.Lexer.Position + 1);
        Assert.Equal(13, second.ObjectNumber);
        Assert.Equal(5, Assert.IsType<PdfInteger>(second.Value).Value);
    }

    [Theory]
    [InlineData("<< >>")]
    [InlineData("1 obj 2 endobj")]
    [InlineData("0 0 obj 2 endobj")]
    [InlineData("1 70000 obj 2 endobj")]
    [InlineData("1 0 endobj")]
    public void IndirectObjectNeedsAHeader(string text)
    {
        Assert.Throws<PdfException>(() => Parser(text).ParseIndirectObject(0));
    }

    [Fact]
    public void ReadsStreamWithItsLength()
    {
        ObjectParser parser = Parser("1 0 obj\n<< /Length 5 >>\nstream\r\nab\ncd\nendstream\nendobj", strict: true);

        PdfStream stream = Assert.IsType<PdfStream>(parser.ParseIndirectObject(0).Value);

        Assert.Equal("ab\ncd"u8.ToArray(), stream.GetEncodedBytes());
        Assert.Equal(5, stream.Dictionary.GetInt("Length"));
    }

    [Fact]
    public void ReadsStreamWithAnIndirectLength()
    {
        Resolver resolver = new(new() { [9] = new PdfInteger(3) });
        ObjectParser parser = Parser("1 0 obj << /Length 9 0 R >> stream\nxyz\nendstream endobj", strict: true, resolver: resolver);

        Assert.Equal("xyz"u8.ToArray(), Assert.IsType<PdfStream>(parser.ParseIndirectObject(0).Value).GetEncodedBytes());
    }

    [Fact]
    public void StreamDataMayHoldEndstreamWhenTheLengthSaysSo()
    {
        ObjectParser parser = Parser("1 0 obj << /Length 13 >> stream\nendstream abc\nendstream endobj", strict: true);

        Assert.Equal("endstream abc"u8.ToArray(), Assert.IsType<PdfStream>(parser.ParseIndirectObject(0).Value).GetEncodedBytes());
    }

    [Theory]
    [InlineData("<< /Length 99 >>", "data", PdfWarningCode.WrongStreamLength)]
    [InlineData("<< /Length 2 >>", "data", PdfWarningCode.WrongStreamLength)]
    [InlineData("<< >>", "data", PdfWarningCode.WrongStreamLength)]
    [InlineData("<< /Length (x) >>", "da\r\nta", PdfWarningCode.WrongStreamLength)]
    [InlineData("<< /Length 5 0 R >>", "data", PdfWarningCode.WrongStreamLength)]
    public void RepairsAWrongLength(string dictionary, string data, PdfWarningCode code)
    {
        ObjectParser parser = Parser($"1 0 obj {dictionary} stream\r\n{data}\r\nendstream\nendobj 2 0 obj null endobj");

        IndirectObject result = parser.ParseIndirectObject(0);

        Assert.Equal(Encoding.Latin1.GetBytes(data), Assert.IsType<PdfStream>(result.Value).GetEncodedBytes());
        Assert.Equal([code], Warnings(parser));
        Assert.Equal(2, parser.ParseIndirectObject(parser.Lexer.Position).ObjectNumber);
    }

    [Fact]
    public void RepairsAMissingEndstream()
    {
        ObjectParser parser = Parser("1 0 obj << /Length 99 >> stream\ndata\nendobj");

        Assert.Equal("data"u8.ToArray(), Assert.IsType<PdfStream>(parser.ParseIndirectObject(0).Value).GetEncodedBytes());
        Assert.Equal([PdfWarningCode.MissingEndstream], Warnings(parser));
    }

    [Fact]
    public void StreamRunsToTheEndWhenNothingCloses()
    {
        ObjectParser parser = Parser("1 0 obj << >> stream\ndata");

        Assert.Equal("data"u8.ToArray(), Assert.IsType<PdfStream>(parser.ParseIndirectObject(0).Value).GetEncodedBytes());
        Assert.Contains(PdfWarningCode.MissingEndstream, Warnings(parser));
    }

    [Theory]
    [InlineData("stream \r\n", PdfWarningCode.MalformedStreamKeyword)]
    [InlineData("stream\r", PdfWarningCode.MalformedStreamKeyword)]
    [InlineData("stream", PdfWarningCode.MalformedStreamKeyword)]
    public void RepairsTheStreamKeywordLine(string keyword, PdfWarningCode code)
    {
        ObjectParser parser = Parser($"1 0 obj << /Length 4 >> {keyword}data\nendstream endobj");

        Assert.Equal("data"u8.ToArray(), Assert.IsType<PdfStream>(parser.ParseIndirectObject(0).Value).GetEncodedBytes());
        Assert.Equal([code], Warnings(parser));
    }

    [Fact]
    public void StreamDataIsReadOnlyWhenAsked()
    {
        byte[] file = Encoding.Latin1.GetBytes("1 0 obj << /Length 100000 >> stream\n" + new string('x', 100_000) + "\nendstream endobj");
        using StreamSource source = new(new MemoryStream(file), leaveOpen: false, blockSize: 1024, capacity: 4);
        ObjectParser parser = new(new Lexer(new SourceReader(source), new WarningLog(true)), PdfReadOptions.Default, null);

        PdfStream stream = Assert.IsType<PdfStream>(parser.ParseIndirectObject(0).Value);
        int reads = source.BlockReads;

        Assert.True(reads < 5, $"{reads} blocks read to parse the stream object.");
        Assert.Equal(100_000, stream.EncodedLength);
        Assert.Equal(100_000, stream.GetEncodedBytes().Length);
    }

    [Fact]
    public void RepairsAMissingEndobj()
    {
        ObjectParser parser = Parser("1 0 obj << /A 1 >>\n2 0 obj 3 endobj");

        Assert.Equal("<< /A 1 >>", Write(parser.ParseIndirectObject(0).Value));
        Assert.Equal([PdfWarningCode.MissingEndobj], Warnings(parser));
        Assert.Equal(2, parser.ParseIndirectObject(parser.Lexer.Position).ObjectNumber);
    }

    [Theory]
    [InlineData("[1 2", "[1 2]", PdfWarningCode.UnterminatedObject)]
    [InlineData("<< /A [1 >>", "<< /A [1] >>", PdfWarningCode.UnterminatedObject)]
    [InlineData("<< /A 1 /B >>", "<< /A 1 >>", PdfWarningCode.MalformedDictionary)]
    [InlineData("<< 1 /A 2 >>", "<< /A 2 >>", PdfWarningCode.MalformedDictionary)]
    [InlineData("<< (k) /A 2 >>", "<< /A 2 >>", PdfWarningCode.MalformedDictionary)]
    [InlineData("<< [1] /A 2 >>", "<< /A 2 >>", PdfWarningCode.MalformedDictionary)]
    [InlineData("<< /A 1 /A 2 >>", "<< /A 2 >>", PdfWarningCode.DuplicateKey)]
    [InlineData("<< /A ] 1 >>", "<< /A 1 >>", PdfWarningCode.UnexpectedToken)]
    [InlineData("[1 junk 2]", "[1 2]", PdfWarningCode.UnexpectedToken)]
    [InlineData("<< /A junk /B 2 >>", "<< /B 2 >>", PdfWarningCode.UnexpectedToken)]
    [InlineData("[0 0 R]", "[null]", PdfWarningCode.InvalidReference)]
    [InlineData("[1 70000 R]", "[null]", PdfWarningCode.InvalidReference)]
    [InlineData("[{1}]", "[1]", PdfWarningCode.UnexpectedToken)]
    [InlineData("[-1 0 R]", "[-1 0]", PdfWarningCode.UnexpectedToken)]
    [InlineData("]", "null", PdfWarningCode.UnexpectedToken)]
    [InlineData(">>", "null", PdfWarningCode.UnexpectedToken)]
    [InlineData("", "null", PdfWarningCode.UnexpectedToken)]
    [InlineData("endobj", "null", PdfWarningCode.UnexpectedToken)]
    [InlineData("[1 << /A [2 endobj", "[1 << /A [2] >>]", PdfWarningCode.UnterminatedObject)]
    public void RepairsDamagedObjects(string text, string expected, PdfWarningCode code)
    {
        ObjectParser parser = Parser(text);

        Assert.Equal(expected, Write(parser.ParseObject()));
        Assert.Contains(code, Warnings(parser));
    }

    [Fact]
    public void ObjectEndsAtAStructureKeyword()
    {
        ObjectParser parser = Parser("[1 2 endobj");

        Assert.Equal("[1 2]", Write(parser.ParseObject()));
        Assert.True(parser.Lexer.IsKeyword(parser.Lexer.Next(), "endobj"u8));
    }

    [Fact]
    public void StrictModeThrowsOnDamage()
    {
        PdfException e = Assert.Throws<PdfException>(() => Parser("1 0 obj [1 2 endobj", strict: true).ParseIndirectObject(0));

        Assert.Equal(PdfWarningCode.UnterminatedObject, e.Code);
        Assert.Equal(1, e.ObjectNumber);
        Assert.Equal(8, e.Offset);
    }

    [Fact]
    public void NestingIsLimited()
    {
        PdfReadOptions options = new() { MaxNestingDepth = 10 };

        Assert.Equal(10, CountDepth(Parser(new string('[', 10) + new string(']', 10), options: options).ParseObject()));
        PdfLimitException e = Assert.Throws<PdfLimitException>(() => Parser(new string('[', 11), options: options).ParseObject());
        Assert.Equal("MaxNestingDepth", e.Limit);
        Assert.Equal(10, e.Offset);
    }

    [Fact]
    public void DeepInputCannotOverflowTheStack()
    {
        PdfReadOptions options = new() { MaxNestingDepth = 1_000_000 };
        string text = string.Concat(Enumerable.Repeat("<</A ", 200_000)) + "1";

        PdfObject value = Parser(text, options: options).ParseObject();

        Assert.IsType<PdfDictionary>(value);
    }

    private static int CountDepth(PdfObject value)
    {
        int depth = 0;
        while (value is PdfArray { Count: > 0 } array)
        {
            depth++;
            value = array[0];
        }

        return value is PdfArray ? depth + 1 : depth;
    }
}
