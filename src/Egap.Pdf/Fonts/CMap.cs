using System.Globalization;
using System.Text;

namespace Egap.Pdf.Fonts;

/// <summary>
/// A CMap (9.7.5, 9.10.3 and Adobe Technical Note 5014): codespace ranges that split byte strings into codes, and maps from
/// codes to CIDs (an encoding CMap) or to Unicode text (a ToUnicode CMap). Both kinds are read by one lenient parser; what a
/// file gets wrong is skipped, not thrown.
/// </summary>
internal sealed class CMap
{
    /// <summary>The most mappings one CMap may hold, against files that declare huge ranges.</summary>
    private const int MaxMappings = 1 << 20;

    private readonly List<(byte[] Low, byte[] High)> codespaces = [];
    private readonly Dictionary<(int Length, int Code), int> cids = [];
    private readonly List<(int Length, int Low, int High, int Cid)> cidRanges = [];
    private readonly Dictionary<(int Length, int Code), string> unicode = [];
    private readonly List<(int Length, int Low, int High, string Start)> unicodeRanges = [];

    /// <summary>The CMap's name, from <c>/CMapName</c>, when it gives one.</summary>
    public string? Name { get; private set; }

    /// <summary>Whether the CMap writes vertically (<c>/WMode 1</c>).</summary>
    public bool IsVertical { get; private set; }

    /// <summary>The name of the CMap this one builds on (<c>usecmap</c>), when there is one.</summary>
    public string? UseCMap { get; private set; }

    /// <summary>Whether the CMap maps any code to Unicode.</summary>
    public bool HasUnicode => unicode.Count > 0 || unicodeRanges.Count > 0;

    /// <summary>Whether the CMap maps any code to a CID.</summary>
    public bool HasCids => cids.Count > 0 || cidRanges.Count > 0;

    /// <summary>Reads a CMap from its PostScript-like text; never throws.</summary>
    public static CMap Parse(ReadOnlySpan<byte> data)
    {
        CMap map = new();
        map.Read(data);
        return map;
    }

    /// <summary>Takes the codespaces and mappings of the CMap this one uses, under its own.</summary>
    public void Include(CMap parent)
    {
        if (codespaces.Count == 0)
        {
            codespaces.AddRange(parent.codespaces);
        }

        foreach (KeyValuePair<(int, int), int> entry in parent.cids)
        {
            cids.TryAdd(entry.Key, entry.Value);
        }

        cidRanges.AddRange(parent.cidRanges);
        foreach (KeyValuePair<(int, int), string> entry in parent.unicode)
        {
            unicode.TryAdd(entry.Key, entry.Value);
        }

        unicodeRanges.AddRange(parent.unicodeRanges);
    }

    /// <summary>
    /// The next code in <paramref name="bytes"/> from <paramref name="offset"/> and how many bytes it takes: the shortest
    /// codespace range it falls in, as 9.7.6.2 says; bytes that match none are read as one code of the shortest length that
    /// has a range starting with a matching first byte, else as one byte.
    /// </summary>
    public (int Code, int Length) Next(ReadOnlySpan<byte> bytes, int offset)
    {
        if (codespaces.Count == 0)
        {
            return (bytes[offset], 1);
        }

        int code = 0;
        for (int length = 1; length <= 4 && offset + length <= bytes.Length; length++)
        {
            code = (code << 8) | bytes[offset + length - 1];
            foreach ((byte[] low, byte[] high) in codespaces)
            {
                if (low.Length == length && InRange(bytes.Slice(offset, length), low, high))
                {
                    return (code, length);
                }
            }
        }

        // Not in any range (9.7.6.3): take as many bytes as the shortest range whose first byte matches.
        byte first = bytes[offset];
        int fallback = codespaces.Where(c => first >= c.Low[0] && first <= c.High[0]).Select(c => c.Low.Length).DefaultIfEmpty(1).Min();
        fallback = Math.Min(fallback, bytes.Length - offset);
        int value = 0;
        for (int i = 0; i < fallback; i++)
        {
            value = (value << 8) | bytes[offset + i];
        }

        return (value, fallback);
    }

    /// <summary>How many bytes a code takes: the shortest codespace range holding its value, else <paramref name="fallback"/>.</summary>
    public int LengthOf(int code, int fallback)
    {
        Span<byte> bytes = stackalloc byte[4];
        for (int length = 1; length <= 4; length++)
        {
            if (length < 4 && code >= 1 << (8 * length))
            {
                continue;
            }

            for (int i = 0; i < length; i++)
            {
                bytes[i] = (byte)(code >> (8 * (length - 1 - i)));
            }

            foreach ((byte[] low, byte[] high) in codespaces)
            {
                if (low.Length == length && InRange(bytes[..length], low, high))
                {
                    return length;
                }
            }
        }

        return fallback;
    }

    /// <summary>The CID a code maps to, or null.</summary>
    public int? ToCid(int code, int length)
    {
        if (cids.TryGetValue((length, code), out int cid))
        {
            return cid;
        }

        for (int i = cidRanges.Count - 1; i >= 0; i--)
        {
            (int l, int low, int high, int start) = cidRanges[i];
            if (l == length && code >= low && code <= high)
            {
                return start + (code - low);
            }
        }

        return null;
    }

    /// <summary>
    /// The text a code maps to, or null. A ToUnicode map whose codes are longer or shorter than the font's (a common
    /// producer error) is still found by the code's value.
    /// </summary>
    public string? ToUnicode(int code, int length)
    {
        return Lookup(code, length) ?? Lookup(code, 0);
    }

    private string? Lookup(int code, int length)
    {
        if (length > 0 && unicode.TryGetValue((length, code), out string? text))
        {
            return text;
        }

        if (length == 0)
        {
            for (int l = 1; l <= 4; l++)
            {
                if (unicode.TryGetValue((l, code), out text))
                {
                    return text;
                }
            }
        }

        for (int i = unicodeRanges.Count - 1; i >= 0; i--)
        {
            (int l, int low, int high, string start) = unicodeRanges[i];
            if ((length == 0 || l == length) && code >= low && code <= high)
            {
                return Increment(start, code - low);
            }
        }

        return null;
    }

    /// <summary>A bfrange's text for the n-th code: the start with its last UTF-16 unit raised by n (9.10.3).</summary>
    private static string Increment(string start, int n)
    {
        if (n == 0 || start.Length == 0)
        {
            return start;
        }

        // A start ending in a surrogate pair counts in code points, as pdf.js does.
        if (start.Length >= 2 && char.IsSurrogatePair(start[^2], start[^1]))
        {
            int cp = char.ConvertToUtf32(start[^2], start[^1]) + n;
            return cp <= 0x10FFFF ? start[..^2] + char.ConvertFromUtf32(cp) : start;
        }

        int last = start[^1] + n;
        return last <= 0xFFFF ? start[..^1] + (char)last : start;
    }

    private static bool InRange(ReadOnlySpan<byte> code, byte[] low, byte[] high)
    {
        for (int i = 0; i < code.Length; i++)
        {
            if (code[i] < low[i] || code[i] > high[i])
            {
                return false;
            }
        }

        return true;
    }

    private void Read(ReadOnlySpan<byte> data)
    {
        Tokenizer tokens = new(data);
        List<Token> operands = [];
        int mappings = 0;
        while (tokens.Next() is Token token)
        {
            if (token.Kind != TokenKind.Keyword)
            {
                operands.Add(token);
                if (operands.Count > 4096)
                {
                    operands.RemoveRange(0, 2048);
                }

                continue;
            }

            switch (token.Text)
            {
                case "def" when operands.Count >= 2 && operands[^2].Kind == TokenKind.Name:
                    string key = operands[^2].Text;
                    Token value = operands[^1];
                    if (key == "CMapName" && value.Kind == TokenKind.Name)
                    {
                        Name = value.Text;
                    }
                    else if (key == "WMode" && value.Kind == TokenKind.Number)
                    {
                        IsVertical = value.Number == 1;
                    }

                    break;
                case "usecmap" when operands.Count >= 1 && operands[^1].Kind == TokenKind.Name:
                    UseCMap = operands[^1].Text;
                    break;
                case "begincodespacerange":
                case "begincidrange":
                case "begincidchar":
                case "beginbfrange":
                case "beginbfchar":
                case "beginnotdefrange":
                case "beginnotdefchar":
                    operands.Clear();
                    break;
                case "endcodespacerange":
                    for (int i = 0; i + 1 < operands.Count; i += 2)
                    {
                        if (operands[i].Bytes is { Length: > 0 and <= 4 } low && operands[i + 1].Bytes is { } high && high.Length == low.Length)
                        {
                            codespaces.Add((low, high));
                        }
                    }

                    operands.Clear();
                    break;
                case "endcidrange":
                    for (int i = 0; i + 2 < operands.Count && mappings < MaxMappings; i += 3)
                    {
                        if (operands[i].Bytes is { Length: > 0 and <= 4 } low && operands[i + 1].Bytes is { } high && operands[i + 2].Kind == TokenKind.Number)
                        {
                            cidRanges.Add((low.Length, Value(low), Value(high), (int)operands[i + 2].Number));
                            mappings++;
                        }
                    }

                    operands.Clear();
                    break;
                case "endcidchar":
                    for (int i = 0; i + 1 < operands.Count && mappings < MaxMappings; i += 2)
                    {
                        if (operands[i].Bytes is { Length: > 0 and <= 4 } code && operands[i + 1].Kind == TokenKind.Number)
                        {
                            cids[(code.Length, Value(code))] = (int)operands[i + 1].Number;
                            mappings++;
                        }
                    }

                    operands.Clear();
                    break;
                case "endbfchar":
                    for (int i = 0; i + 1 < operands.Count && mappings < MaxMappings; i += 2)
                    {
                        if (operands[i].Bytes is { Length: > 0 and <= 4 } code && Text(operands[i + 1]) is string text)
                        {
                            unicode[(code.Length, Value(code))] = text;
                            mappings++;
                        }
                    }

                    operands.Clear();
                    break;
                case "endbfrange":
                    ReadBfRanges(operands, ref mappings);
                    operands.Clear();
                    break;
                case "endnotdefrange":
                case "endnotdefchar":
                    operands.Clear();
                    break;
                case "]":
                    // An array closes: gather the tokens since its opening bracket into one array token.
                    int open = operands.FindLastIndex(t => t.Kind == TokenKind.ArrayStart);
                    if (open >= 0)
                    {
                        Token array = new(TokenKind.Array, string.Empty) { Items = operands[(open + 1)..] };
                        operands.RemoveRange(open, operands.Count - open);
                        operands.Add(array);
                    }

                    break;
                case "[":
                    operands.Add(new Token(TokenKind.ArrayStart, "["));
                    break;
            }
        }
    }

    private void ReadBfRanges(List<Token> operands, ref int mappings)
    {
        for (int i = 0; i + 2 < operands.Count && mappings < MaxMappings; i += 3)
        {
            if (operands[i].Bytes is not { Length: > 0 and <= 4 } low || operands[i + 1].Bytes is not { } high || high.Length != low.Length)
            {
                continue;
            }

            int from = Value(low), to = Value(high);
            if (to < from)
            {
                continue;
            }

            if (operands[i + 2].Kind == TokenKind.Array)
            {
                List<Token> items = operands[i + 2].Items!;
                for (int k = 0; k < items.Count && from + k <= to && mappings < MaxMappings; k++)
                {
                    if (Text(items[k]) is string text)
                    {
                        unicode[(low.Length, from + k)] = text;
                        mappings++;
                    }
                }
            }
            else if (Text(operands[i + 2]) is string start)
            {
                unicodeRanges.Add((low.Length, from, to, start));
                mappings += Math.Min(to - from + 1, 256);
            }
        }
    }

    /// <summary>A destination as text: UTF-16BE bytes, or a glyph name (an old form some producers still write).</summary>
    private static string? Text(Token token)
    {
        if (token.Bytes is byte[] bytes)
        {
            if (bytes.Length == 1)
            {
                return ((char)bytes[0]).ToString();
            }

            return Encoding.BigEndianUnicode.GetString(bytes, 0, bytes.Length & ~1);
        }

        return token.Kind == TokenKind.Name ? GlyphList.ToUnicode(token.Text) : null;
    }

    private static int Value(byte[] bytes)
    {
        int value = 0;
        foreach (byte b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    private enum TokenKind
    {
        Number,
        Name,
        String,
        Keyword,
        ArrayStart,
        Array,
    }

    private sealed record Token(TokenKind Kind, string Text)
    {
        public double Number { get; init; }

        public byte[]? Bytes { get; init; }

        public List<Token>? Items { get; init; }
    }

    /// <summary>The PostScript tokens a CMap uses: numbers, names, hex and literal strings, brackets, dictionaries and keywords.</summary>
    private ref struct Tokenizer(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private int at;

        public Token? Next()
        {
            while (at < data.Length)
            {
                byte b = data[at];
                if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0)
                {
                    at++;
                    continue;
                }

                if (b == '%')
                {
                    while (at < data.Length && data[at] is not ((byte)'\r' or (byte)'\n'))
                    {
                        at++;
                    }

                    continue;
                }

                if (b == '<' && at + 1 < data.Length && data[at + 1] == '<')
                {
                    at += 2;
                    return new Token(TokenKind.Keyword, "<<");
                }

                if (b == '>' && at + 1 < data.Length && data[at + 1] == '>')
                {
                    at += 2;
                    return new Token(TokenKind.Keyword, ">>");
                }

                if (b == '<')
                {
                    return Hex();
                }

                if (b == '(')
                {
                    return Literal();
                }

                if (b is (byte)'[' or (byte)']' or (byte)'{' or (byte)'}')
                {
                    at++;
                    return new Token(TokenKind.Keyword, ((char)b).ToString());
                }

                if (b == '/')
                {
                    at++;
                    return new Token(TokenKind.Name, Word());
                }

                string word = Word();
                if (word.Length == 0)
                {
                    at++;
                    continue;
                }

                return double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                    ? new Token(TokenKind.Number, word) { Number = number }
                    : new Token(TokenKind.Keyword, word);
            }

            return null;
        }

        private string Word()
        {
            int start = at;
            while (at < data.Length && data[at] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0 or (byte)'/'
                or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'(' or (byte)')' or (byte)'{' or (byte)'}' or (byte)'%'))
            {
                at++;
            }

            return Encoding.Latin1.GetString(data[start..at]);
        }

        private Token Hex()
        {
            at++;
            List<byte> bytes = [];
            int high = -1;
            while (at < data.Length && data[at] != '>')
            {
                int digit = HexDigit(data[at++]);
                if (digit < 0)
                {
                    continue;
                }

                if (high < 0)
                {
                    high = digit;
                }
                else
                {
                    bytes.Add((byte)((high << 4) | digit));
                    high = -1;
                }
            }

            if (high >= 0)
            {
                bytes.Add((byte)(high << 4));
            }

            at++;
            return new Token(TokenKind.String, string.Empty) { Bytes = [.. bytes] };
        }

        private Token Literal()
        {
            at++;
            List<byte> bytes = [];
            int depth = 1;
            while (at < data.Length)
            {
                byte b = data[at++];
                if (b == '\\' && at < data.Length)
                {
                    byte e = data[at++];
                    switch (e)
                    {
                        case (byte)'n':
                            bytes.Add((byte)'\n');
                            break;
                        case (byte)'r':
                            bytes.Add((byte)'\r');
                            break;
                        case (byte)'t':
                            bytes.Add((byte)'\t');
                            break;
                        case (byte)'b':
                            bytes.Add(8);
                            break;
                        case (byte)'f':
                            bytes.Add(12);
                            break;
                        case >= (byte)'0' and <= (byte)'7':
                            int value = e - '0';
                            for (int k = 0; k < 2 && at < data.Length && data[at] is >= (byte)'0' and <= (byte)'7'; k++)
                            {
                                value = (value * 8) + (data[at++] - '0');
                            }

                            bytes.Add((byte)value);
                            break;
                        case (byte)'\r' or (byte)'\n':
                            break;
                        default:
                            bytes.Add(e);
                            break;
                    }

                    continue;
                }

                if (b == '(')
                {
                    depth++;
                }
                else if (b == ')' && --depth == 0)
                {
                    break;
                }

                bytes.Add(b);
            }

            return new Token(TokenKind.String, string.Empty) { Bytes = [.. bytes] };
        }

        private static int HexDigit(byte b)
        {
            return b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                _ => -1,
            };
        }
    }
}
