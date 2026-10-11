namespace Egap.Pdf.Parsing;

/// <summary>The kinds of PDF token (ISO 32000-2, 7.2 and 7.3).</summary>
internal enum TokenKind : byte
{
    EndOfData,
    Integer,
    Real,
    LiteralString,
    HexString,
    Name,

    /// <summary>A run of regular characters that is not a number: <c>true</c>, <c>obj</c>, <c>R</c>, operators and so on.</summary>
    Keyword,
    ArrayStart,
    ArrayEnd,
    DictionaryStart,
    DictionaryEnd,
    BraceOpen,
    BraceClose,
}

/// <summary>
/// One token. Numbers carry their value here; strings, names and keywords leave their bytes in <see cref="Lexer.Bytes"/>
/// until the next token is read.
/// </summary>
/// <param name="Kind">The kind of token.</param>
/// <param name="Start">The offset of its first byte.</param>
/// <param name="Integer">The value of an integer.</param>
/// <param name="Real">The value of a real number.</param>
internal readonly record struct Token(TokenKind Kind, long Start, long Integer = 0, double Real = 0);

/// <summary>The PDF character classes (ISO 32000-2, 7.2.3).</summary>
internal static class CharClass
{
    public static bool IsWhiteSpace(int b)
    {
        return b is 0 or 9 or 10 or 12 or 13 or 32;
    }

    public static bool IsDelimiter(int b)
    {
        return b is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';
    }

    public static bool IsRegular(int b)
    {
        return b >= 0 && !IsWhiteSpace(b) && !IsDelimiter(b);
    }

    public static bool IsDigit(int b)
    {
        return (uint)(b - '0') <= 9;
    }

    /// <summary>The value of a hex digit, or -1.</summary>
    public static int HexValue(int b)
    {
        return b switch
        {
            >= '0' and <= '9' => b - '0',
            >= 'A' and <= 'F' => b - 'A' + 10,
            >= 'a' and <= 'f' => b - 'a' + 10,
            _ => -1,
        };
    }
}
