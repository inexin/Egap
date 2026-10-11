using System.Globalization;
using System.Text;

using Egap.Pdf.IO;

namespace Egap.Pdf.Parsing;

/// <summary>
/// Splits PDF bytes into tokens (ISO 32000-2, 7.2 and 7.3). It is lenient the way other readers are: malformed numbers, names
/// and strings are read anyway, each repair recorded in the warning log, and it never throws except in strict mode.
/// </summary>
internal sealed class Lexer
{
    private byte[] buffer = new byte[64];
    private int length;

    public Lexer(SourceReader reader, WarningLog log)
    {
        Reader = reader;
        Log = log;
    }

    public SourceReader Reader { get; }

    public WarningLog Log { get; }

    /// <summary>The number of the object being read, recorded with warnings.</summary>
    public int? ObjectNumber { get; set; }

    public long Position
    {
        get => Reader.Position;
        set => Reader.Position = value;
    }

    /// <summary>
    /// The bytes of the last string, name or keyword token (decoded: escapes resolved), or the text of the last real number
    /// when <see cref="CleanNumber"/> is set.
    /// </summary>
    public ReadOnlySpan<byte> Bytes => buffer.AsSpan(0, length);

    /// <summary>Whether the last number was written exactly as the spec allows, so its text can be kept for writing back.</summary>
    public bool CleanNumber { get; private set; }

    /// <summary>Whether the last token was the given keyword.</summary>
    public bool IsKeyword(Token token, ReadOnlySpan<byte> keyword)
    {
        return token.Kind == TokenKind.Keyword && Bytes.SequenceEqual(keyword);
    }

    /// <summary>Reads the next token, skipping white space and comments.</summary>
    public Token Next()
    {
        while (true)
        {
            SkipWhiteSpaceAndComments();
            long start = Reader.Position;
            int b = Reader.Read();
            switch (b)
            {
                case -1:
                    return new Token(TokenKind.EndOfData, start);
                case '[':
                    return new Token(TokenKind.ArrayStart, start);
                case ']':
                    return new Token(TokenKind.ArrayEnd, start);
                case '{':
                    return new Token(TokenKind.BraceOpen, start);
                case '}':
                    return new Token(TokenKind.BraceClose, start);
                case '(':
                    ReadLiteralString(start);
                    return new Token(TokenKind.LiteralString, start);
                case '/':
                    ReadName(start);
                    return new Token(TokenKind.Name, start);
                case '<':
                    if (Reader.Peek() == '<')
                    {
                        Reader.Skip();
                        return new Token(TokenKind.DictionaryStart, start);
                    }

                    ReadHexString(start);
                    return new Token(TokenKind.HexString, start);
                case '>':
                    if (Reader.Peek() == '>')
                    {
                        Reader.Skip();
                        return new Token(TokenKind.DictionaryEnd, start);
                    }

                    Warn(PdfWarningCode.UnexpectedDelimiter, "A single '>' outside a hex string was skipped.", start);
                    continue;
                case ')':
                    Warn(PdfWarningCode.UnexpectedDelimiter, "A ')' outside a string was skipped.", start);
                    continue;
                default:
                    if (CharClass.IsDigit(b) || b is '+' or '-' or '.')
                    {
                        Reader.Position = start;
                        return ReadNumber(start);
                    }

                    length = 0;
                    Append((byte)b);
                    while (CharClass.IsRegular(Reader.Peek()))
                    {
                        Append((byte)Reader.Read());
                    }

                    return new Token(TokenKind.Keyword, start);
            }
        }
    }

    /// <summary>Skips white space and comments, leaving the reader at the next token.</summary>
    public void SkipWhiteSpaceAndComments()
    {
        while (true)
        {
            int b = Reader.Peek();
            if (CharClass.IsWhiteSpace(b))
            {
                Reader.Skip();
            }
            else if (b == '%')
            {
                do
                {
                    Reader.Skip();
                    b = Reader.Peek();
                }
                while (b is not ('\r' or '\n' or -1));
            }
            else
            {
                return;
            }
        }
    }

    private Token ReadNumber(long start)
    {
        length = 0;
        bool malformed = false;
        int signs = 0;
        bool negative = false;
        while (Reader.Peek() is '+' or '-')
        {
            negative |= Reader.Read() == '-';
            signs++;
        }

        malformed |= signs > 1;
        if (negative)
        {
            Append((byte)'-');
        }

        bool digits = false;
        bool point = false;
        bool exponent = false;
        while (true)
        {
            int b = Reader.Peek();
            if (CharClass.IsDigit(b))
            {
                digits = true;
            }
            else if (b == '.' && !point && !exponent)
            {
                point = true;
            }
            else if (b == '-' && (digits || point) && !exponent)
            {
                // A minus inside a number, as some producers write: ignored, as Adobe Reader and pdf.js do.
                Reader.Skip();
                malformed = true;
                continue;
            }
            else if (b is 'e' or 'E' && digits && !exponent && IsExponent())
            {
                // Exponents are not PDF syntax, but some producers write them.
                exponent = true;
                malformed = true;
                Append((byte)'e');
                Reader.Skip();
                if (Reader.Peek() is '+' or '-')
                {
                    Append((byte)Reader.Read());
                }

                continue;
            }
            else
            {
                break;
            }

            Append((byte)Reader.Read());
        }

        CleanNumber = !malformed && digits;
        if (!digits)
        {
            // A lone sign or point: read as zero, as other readers do.
            Warn(PdfWarningCode.MalformedNumber, $"'{Encoding.ASCII.GetString(Reader.Source.ReadBytes(start, (int)(Reader.Position - start)))}' is not a number; read as 0.", start);
            CheckSeparator();
            return new Token(TokenKind.Integer, start, 0);
        }

        if (malformed)
        {
            Warn(PdfWarningCode.MalformedNumber, $"Malformed number read as {Encoding.ASCII.GetString(Bytes)}.", start);
        }

        CheckSeparator();
        if (!point && !exponent)
        {
            if (long.TryParse(Bytes, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
            {
                return new Token(TokenKind.Integer, start, integer);
            }

            Warn(PdfWarningCode.NumberOutOfRange, $"The integer {Encoding.ASCII.GetString(Bytes)} is too large and was read as a real number.", start);
            CleanNumber = false;
        }

        double real = double.Parse(Bytes, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(real))
        {
            Warn(PdfWarningCode.NumberOutOfRange, $"The number {Encoding.ASCII.GetString(Bytes)} is too large and was read as 0.", start);
            real = 0;
            CleanNumber = false;
        }

        return new Token(TokenKind.Real, start, Real: real);
    }

    private bool IsExponent()
    {
        int next = Reader.Peek(1);
        return CharClass.IsDigit(next) || (next is '+' or '-' && CharClass.IsDigit(Reader.Peek(2)));
    }

    private void CheckSeparator()
    {
        if (CharClass.IsRegular(Reader.Peek()))
        {
            Warn(PdfWarningCode.MissingSeparator, "A number runs straight into the next token.", Reader.Position);
        }
    }

    private void ReadLiteralString(long start)
    {
        length = 0;
        int depth = 1;
        while (true)
        {
            int b = Reader.Read();
            switch (b)
            {
                case -1:
                    Warn(PdfWarningCode.UnterminatedString, "A literal string has no closing ')'; it was read to the end of the data.", start);
                    return;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0)
                    {
                        return;
                    }

                    break;
                case '\r':
                    // An unescaped end-of-line marker of any kind is read as a line feed (7.3.4.2).
                    if (Reader.Peek() == '\n')
                    {
                        Reader.Skip();
                    }

                    b = '\n';
                    break;
                case '\\':
                    b = ReadEscape();
                    if (b == -2)
                    {
                        continue;
                    }

                    if (b == -1)
                    {
                        Warn(PdfWarningCode.UnterminatedString, "A literal string ends in a backslash at the end of the data.", start);
                        return;
                    }

                    break;
            }

            Append((byte)b);
        }
    }

    /// <summary>Reads the escape after a backslash: the byte it stands for, -2 for a line continuation, -1 at the end.</summary>
    private int ReadEscape()
    {
        int b = Reader.Read();
        switch (b)
        {
            case 'n':
                return '\n';
            case 'r':
                return '\r';
            case 't':
                return '\t';
            case 'b':
                return '\b';
            case 'f':
                return '\f';
            case '\r':
                if (Reader.Peek() == '\n')
                {
                    Reader.Skip();
                }

                return -2;
            case '\n':
                return -2;
            case >= '0' and <= '7':
                int value = b - '0';
                for (int i = 0; i < 2 && Reader.Peek() is >= '0' and <= '7'; i++)
                {
                    value = (value * 8) + (Reader.Read() - '0');
                }

                // High-order overflow is ignored (7.3.4.2).
                return value & 0xFF;
            default:
                // Covers \( \) \\ and any other character, whose backslash is ignored (7.3.4.2); -1 at the end.
                return b;
        }
    }

    private void ReadHexString(long start)
    {
        length = 0;
        int high = -1;
        bool warned = false;
        while (true)
        {
            int b = Reader.Read();
            if (b is '>' or -1)
            {
                if (b == -1)
                {
                    Warn(PdfWarningCode.MalformedHexString, "A hex string has no closing '>'; it was read to the end of the data.", start);
                }

                break;
            }

            int value = CharClass.HexValue(b);
            if (value >= 0)
            {
                if (high < 0)
                {
                    high = value;
                }
                else
                {
                    Append((byte)((high << 4) | value));
                    high = -1;
                }
            }
            else if (!CharClass.IsWhiteSpace(b) && !warned)
            {
                Warn(PdfWarningCode.MalformedHexString, $"A hex string holds the character 0x{b:X2}, which was skipped.", Reader.Position - 1);
                warned = true;
            }
        }

        // An odd final digit is taken as followed by 0 (7.3.4.3).
        if (high >= 0)
        {
            Append((byte)(high << 4));
        }
    }

    private void ReadName(long start)
    {
        length = 0;
        while (CharClass.IsRegular(Reader.Peek()))
        {
            int b = Reader.Read();
            if (b == '#')
            {
                int high = CharClass.HexValue(Reader.Peek());
                int low = high >= 0 ? CharClass.HexValue(Reader.Peek(1)) : -1;
                if (low >= 0)
                {
                    Reader.Skip();
                    Reader.Skip();
                    b = (high << 4) | low;
                }
                else
                {
                    Warn(PdfWarningCode.MalformedName, "A '#' in a name is not followed by two hex digits; it was kept as is.", start);
                }
            }

            Append((byte)b);
        }
    }

    private void Append(byte b)
    {
        if (length == buffer.Length)
        {
            Array.Resize(ref buffer, buffer.Length * 2);
        }

        buffer[length++] = b;
    }

    private void Warn(PdfWarningCode code, string message, long offset)
    {
        Log.Add(code, message, offset, ObjectNumber);
    }
}
