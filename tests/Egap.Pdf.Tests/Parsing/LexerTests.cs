using System.Text;

using Egap.Pdf.IO;
using Egap.Pdf.Parsing;

namespace Egap.Pdf.Tests.Parsing;

public sealed class LexerTests
{
    private static Lexer Lex(string text, bool strict = false)
    {
        return Lex(Encoding.Latin1.GetBytes(text), strict);
    }

    private static Lexer Lex(byte[] bytes, bool strict = false)
    {
        return new Lexer(new SourceReader(PdfSource.FromMemory(bytes)), new WarningLog(strict));
    }

    private static string Text(Lexer lexer)
    {
        return Encoding.Latin1.GetString(lexer.Bytes);
    }

    /// <summary>Each token as text: kind and value.</summary>
    private static List<string> All(Lexer lexer)
    {
        List<string> tokens = [];
        while (true)
        {
            Token token = lexer.Next();
            string value = token.Kind switch
            {
                TokenKind.EndOfData => "",
                TokenKind.Integer => token.Integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TokenKind.Real => token.Real.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                TokenKind.LiteralString or TokenKind.HexString or TokenKind.Name or TokenKind.Keyword => Text(lexer),
                _ => "",
            };
            tokens.Add(value.Length == 0 ? token.Kind.ToString() : $"{token.Kind}:{value}");
            if (token.Kind == TokenKind.EndOfData)
            {
                return tokens;
            }
        }
    }

    [Fact]
    public void ReadsEveryTokenKind()
    {
        Lexer lexer = Lex("<< /Type /Page /K [1 -2 +3 4.5 -.5 6. true null] >> (str) <414243> { R } 12 0 obj");

        Assert.Equal(
            [
                "DictionaryStart", "Name:Type", "Name:Page", "Name:K", "ArrayStart", "Integer:1", "Integer:-2", "Integer:3", "Real:4.5",
                "Real:-0.5", "Real:6", "Keyword:true", "Keyword:null", "ArrayEnd", "DictionaryEnd", "LiteralString:str", "HexString:ABC",
                "BraceOpen", "Keyword:R", "BraceClose", "Integer:12", "Integer:0", "Keyword:obj", "EndOfData",
            ],
            All(lexer));
        Assert.Equal(0, lexer.Log.Count);
    }

    [Fact]
    public void RecordsTokenOffsets()
    {
        Lexer lexer = Lex("  /A  12 % comment\n(x)");

        Assert.Equal(2, lexer.Next().Start);
        Assert.Equal(6, lexer.Next().Start);
        Assert.Equal(19, lexer.Next().Start);
    }

    [Fact]
    public void SkipsCommentsAndEveryWhiteSpaceByte()
    {
        Lexer lexer = Lex("%PDF-1.7\r\n\0\t\f 1%x\r2%end");

        Assert.Equal(["Integer:1", "Integer:2", "EndOfData"], All(lexer));
    }

    [Theory]
    [InlineData("(a(b)c)", "a(b)c")]
    [InlineData(@"(\n\r\t\b\f\(\)\\)", "\n\r\t\b\f()\\")]
    [InlineData(@"(\101\0533\7)", "A+3\a")]
    [InlineData(@"(\501)", "A")]
    [InlineData("(a\\\r\nb\\\nc\\\rd)", "abcd")]
    [InlineData("(a\r\nb\rc\nd)", "a\nb\nc\nd")]
    [InlineData(@"(\q)", "q")]
    [InlineData("()", "")]
    public void ReadsLiteralStrings(string text, string expected)
    {
        Lexer lexer = Lex(text);

        Assert.Equal(TokenKind.LiteralString, lexer.Next().Kind);
        Assert.Equal(expected, Text(lexer));
        Assert.Equal(0, lexer.Log.Count);
    }

    [Theory]
    [InlineData("<48 65\n6C6c 6F>", "Hello")]
    [InlineData("<901FA>", "\u0090\u001f ")]
    [InlineData("<>", "")]
    public void ReadsHexStrings(string text, string expected)
    {
        Lexer lexer = Lex(text);

        Assert.Equal(TokenKind.HexString, lexer.Next().Kind);
        Assert.Equal(expected, Text(lexer));
        Assert.Equal(0, lexer.Log.Count);
    }

    [Theory]
    [InlineData("/Name1", "Name1")]
    [InlineData("/A;Name_With-Various***Characters?", "A;Name_With-Various***Characters?")]
    [InlineData("/1.2", "1.2")]
    [InlineData("/lime#20Green", "lime Green")]
    [InlineData("/paired#28#29parentheses", "paired()parentheses")]
    [InlineData("/The_Key_of_F#23_Minor", "The_Key_of_F#_Minor")]
    [InlineData("/", "")]
    public void ReadsNames(string text, string expected)
    {
        Lexer lexer = Lex(text);

        Assert.Equal(TokenKind.Name, lexer.Next().Kind);
        Assert.Equal(expected, Text(lexer));
        Assert.Equal(0, lexer.Log.Count);
    }

    [Fact]
    public void NameEndsAtDelimiters()
    {
        Assert.Equal(["Name:A", "Name:B", "ArrayStart", "Name:C", "ArrayEnd", "EndOfData"], All(Lex("/A/B[/C]")));
    }

    [Theory]
    [InlineData("--5", "Integer:-5")]
    [InlineData("+-5", "Integer:-5")]
    [InlineData("4-2", "Integer:42")]
    [InlineData("0.00-50", "Real:0.005")]
    [InlineData("1.5e3", "Real:1500")]
    [InlineData("-", "Integer:0")]
    [InlineData(".", "Integer:0")]
    public void RepairsMalformedNumbers(string text, string expected)
    {
        Lexer lexer = Lex(text);

        Assert.Equal([expected, "EndOfData"], All(lexer));
        Assert.Contains(lexer.Log.ToArray(), w => w.Code == PdfWarningCode.MalformedNumber);
        Assert.False(lexer.CleanNumber);
    }

    [Fact]
    public void SecondPointStartsANewNumber()
    {
        Lexer lexer = Lex("1.2.3");

        Assert.Equal(["Real:1.2", "Real:0.3", "EndOfData"], All(lexer));
    }

    [Fact]
    public void HugeIntegerBecomesReal()
    {
        Lexer lexer = Lex("99999999999999999999");

        Token token = lexer.Next();
        Assert.Equal(TokenKind.Real, token.Kind);
        Assert.Equal(1e20, token.Real);
        Assert.Equal(PdfWarningCode.NumberOutOfRange, Assert.Single(lexer.Log.ToArray()).Code);
    }

    [Fact]
    public void KeepsCleanNumberText()
    {
        Lexer lexer = Lex("-001.500");

        Token token = lexer.Next();
        Assert.Equal(-1.5, token.Real);
        Assert.True(lexer.CleanNumber);
        Assert.Equal("-001.500", Text(lexer));
    }

    [Fact]
    public void WarnsWhenTokensRunTogether()
    {
        Lexer lexer = Lex("12obj");

        Assert.Equal(["Integer:12", "Keyword:obj", "EndOfData"], All(lexer));
        Assert.Equal(PdfWarningCode.MissingSeparator, Assert.Single(lexer.Log.ToArray()).Code);
    }

    [Theory]
    [InlineData("(abc", PdfWarningCode.UnterminatedString, "abc")]
    [InlineData("(abc\\", PdfWarningCode.UnterminatedString, "abc")]
    [InlineData("<41 4G 42>", PdfWarningCode.MalformedHexString, "AD ")]
    [InlineData("<4142", PdfWarningCode.MalformedHexString, "AB")]
    [InlineData("/A#4", PdfWarningCode.MalformedName, "A#4")]
    [InlineData("/A#zz", PdfWarningCode.MalformedName, "A#zz")]
    public void RepairsDamagedTokens(string text, PdfWarningCode code, string expected)
    {
        Lexer lexer = Lex(text);

        lexer.Next();
        Assert.Equal(expected, Text(lexer));
        Assert.Equal(code, Assert.Single(lexer.Log.ToArray()).Code);
    }

    [Fact]
    public void SkipsStrayDelimiters()
    {
        Lexer lexer = Lex(") > /A");

        Assert.Equal(["Name:A", "EndOfData"], All(lexer));
        Assert.All(lexer.Log.ToArray(), w => Assert.Equal(PdfWarningCode.UnexpectedDelimiter, w.Code));
        Assert.Equal(2, lexer.Log.Count);
    }

    [Fact]
    public void StrictModeThrowsInsteadOfWarning()
    {
        PdfException e = Assert.Throws<PdfException>(() => Lex("--5", strict: true).Next());

        Assert.Equal(PdfWarningCode.MalformedNumber, e.Code);
        Assert.Equal(0, e.Offset);
    }

    [Fact]
    public void WarningsRecordObjectNumberAndOffset()
    {
        Lexer lexer = Lex("1 /A#4");
        lexer.ObjectNumber = 7;

        All(lexer);
        PdfWarning warning = Assert.Single(lexer.Log.ToArray());
        Assert.Equal(7, warning.ObjectNumber);
        Assert.Equal(2, warning.Offset);
        Assert.StartsWith("MalformedName in object 7 at byte 2: ", warning.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LongTokensGrowTheBuffer()
    {
        string name = new('x', 10_000);
        Lexer lexer = Lex("/" + name);

        lexer.Next();
        Assert.Equal(name, Text(lexer));
    }

    [Fact]
    public void TokensSpanCacheBlocks()
    {
        // Tokens that cross the 32 KiB block boundary of a stream source read the same as from memory.
        StringBuilder text = new();
        Random random = new(3);
        while (text.Length < 200_000)
        {
            text.Append(random.Next(5) switch
            {
                0 => $"{random.Next()} ",
                1 => $"/N{random.Next()}#20x ",
                2 => $"({new string('s', random.Next(100))}\\101) ",
                3 => $"<{random.Next():X8}> ",
                _ => FormattableString.Invariant($"{random.NextDouble():F6} "),
            });
        }

        byte[] bytes = Encoding.Latin1.GetBytes(text.ToString());
        Lexer fromMemory = Lex(bytes);
        Lexer fromStream = new(new SourceReader(PdfSource.FromStream(new MemoryStream(bytes), leaveOpen: false)), new WarningLog(strict: true));

        Assert.Equal(All(fromMemory), All(fromStream));
    }
}
