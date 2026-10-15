using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Egap.Pdf.Fonts;

/// <summary>
/// A Type 1 font program (Adobe Type 1 Font Format) turned into a bare CFF font that Egami.Fonts loads (Phase 3 design Q8,
/// as pdf.js does). The clear text gives the name, matrix, box and built-in encoding; the eexec-encrypted part gives the
/// subroutines and charstrings. Each charstring is run, with its subroutines, flex, hint replacement and accented
/// characters (<c>seac</c>), into an outline that is written again as a Type 2 charstring without hints: outlines are all
/// text extraction and drawing need. A program that cannot be read throws <see cref="InvalidDataException"/>.
/// </summary>
internal sealed class Type1Font
{
    /// <summary>The most glyphs and subroutines read, and the deepest subroutine nesting, against hostile programs.</summary>
    private const int MaxGlyphs = 65535;
    private const int MaxSubrs = 65535;
    private const int MaxDepth = 10;

    private static readonly System.Buffers.SearchValues<byte> HexDigits = System.Buffers.SearchValues.Create("0123456789abcdefABCDEF"u8);

    private readonly Dictionary<string, byte[]> charStrings = new(StringComparer.Ordinal);
    private readonly List<byte[]?> subrs = [];
    private int lenIV = 4;

    private Type1Font(ReadOnlySpan<byte> program)
    {
        byte[] data = StripPfb(program);
        int eexec = IndexOf(data, "eexec"u8);
        if (eexec < 0)
        {
            throw new InvalidDataException("The Type 1 program has no eexec section.");
        }

        ReadClearText(data.AsSpan(0, eexec));
        int start = eexec + 5;
        while (start < data.Length && data[start] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')
        {
            start++;
        }

        byte[] encrypted = data[start..];
        if (encrypted.Length >= 4 && encrypted.AsSpan(0, 4).IndexOfAnyExcept(HexDigits) < 0)
        {
            encrypted = FromHex(encrypted);
        }

        ReadPrivate(Decrypt(encrypted, 55665, 4));
        if (charStrings.Count == 0)
        {
            throw new InvalidDataException("The Type 1 program has no charstrings.");
        }

        Cff = WriteCff();
    }

    /// <summary>The font's name from <c>/FontName</c>.</summary>
    public string FontName { get; private set; } = "Type1";

    /// <summary>The glyph names of the font's own encoding by code (StandardEncoding when the program says so).</summary>
    public string?[] BuiltInEncoding { get; private set; } = FontEncodings.Standard;

    /// <summary>The bare CFF font the program becomes, glyph 0 being <c>.notdef</c>.</summary>
    public byte[] Cff { get; }

    /// <summary>The glyph names in CFF glyph order.</summary>
    public List<string> GlyphNames { get; } = [];

    private double[] FontMatrix { get; set; } = [0.001, 0, 0, 0.001, 0, 0];

    private double[] FontBBox { get; set; } = [0, 0, 0, 0];

    public static Type1Font Read(ReadOnlySpan<byte> program)
    {
        return new Type1Font(program);
    }

    /// <summary>A PFB file's segments joined: some producers embed the file as it is.</summary>
    private static byte[] StripPfb(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6 || data[0] != 0x80)
        {
            return data.ToArray();
        }

        List<byte> joined = [];
        int at = 0;
        while (at + 6 <= data.Length && data[at] == 0x80 && data[at + 1] is 1 or 2)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(data[(at + 2)..]);
            at += 6;
            if (length < 0 || at + length > data.Length)
            {
                break;
            }

            joined.AddRange(data.Slice(at, length));
            at += length;
        }

        return [.. joined];
    }

    private void ReadClearText(ReadOnlySpan<byte> text)
    {
        string clear = Encoding.Latin1.GetString(text);
        if (Between(clear, "/FontName", "def") is string name)
        {
            FontName = name.Trim().TrimStart('/');
        }

        if (Numbers(Between(clear, "/FontMatrix", "readonly") ?? Between(clear, "/FontMatrix", "def")) is { Length: 6 } matrix)
        {
            FontMatrix = matrix;
        }

        if (Numbers(Between(clear, "/FontBBox", "readonly") ?? Between(clear, "/FontBBox", "def")) is { Length: 4 } box)
        {
            FontBBox = box;
        }

        int encoding = clear.IndexOf("/Encoding", StringComparison.Ordinal);
        if (encoding >= 0 && !clear.AsSpan(encoding).StartsWith("/Encoding StandardEncoding", StringComparison.Ordinal))
        {
            // dup code /name put, for as many entries as the array has.
            string?[] names = new string?[256];
            int at = encoding;
            while ((at = clear.IndexOf("dup ", at, StringComparison.Ordinal)) >= 0)
            {
                string[] parts = clear[at..Math.Min(clear.Length, at + 80)].Split((char[])[' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
                at += 4;
                if (parts.Length >= 4 && parts[3] == "put" && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)
                    && code is >= 0 and < 256 && parts[2].StartsWith('/'))
                {
                    names[code] = parts[2][1..];
                }
                else if (parts.Length >= 1 && parts[0] != "dup")
                {
                    break;
                }

                if (clear.AsSpan(at).TrimStart().StartsWith("readonly def", StringComparison.Ordinal))
                {
                    break;
                }
            }

            if (names.Any(n => n is not null))
            {
                BuiltInEncoding = names;
            }
        }
    }

    private static string? Between(string text, string key, string end)
    {
        int start = text.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += key.Length;
        int stop = text.IndexOf(end, start, StringComparison.Ordinal);
        return stop < 0 || stop - start > 200 ? null : text[start..stop];
    }

    private static double[]? Numbers(string? text)
    {
        if (text is null)
        {
            return null;
        }

        List<double> numbers = [];
        foreach (string part in text.Split((char[])[' ', '[', ']', '{', '}', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                numbers.Add(value);
            }
        }

        return [.. numbers];
    }

    /// <summary>Reads <c>/lenIV</c>, <c>/Subrs</c> and <c>/CharStrings</c> from the decrypted private part.</summary>
    private void ReadPrivate(byte[] data)
    {
        int lenIVAt = IndexOf(data, "/lenIV"u8);
        if (lenIVAt >= 0 && Token(data, lenIVAt + 6, out _) is string len && int.TryParse(len, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            lenIV = value;
        }

        int subrsAt = IndexOf(data, "/Subrs"u8);
        if (subrsAt >= 0)
        {
            // n array, then n times: dup index length RD <binary> NP
            Token(data, subrsAt + 6, out int at);
            if (Token(data, at, out int afterArray) == "array")
            {
                at = afterArray;
            }

            while (subrs.Count <= MaxSubrs)
            {
                if (Token(data, at, out int next) != "dup")
                {
                    break;
                }

                if (!int.TryParse(Token(data, next, out next), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                    || !int.TryParse(Token(data, next, out next), NumberStyles.Integer, CultureInfo.InvariantCulture, out int length)
                    || Token(data, next, out next) is null || next + 1 + length > data.Length || index is < 0 or > MaxSubrs || length < 0)
                {
                    break;
                }

                byte[] encrypted = data.AsSpan(next + 1, length).ToArray();
                while (subrs.Count <= index)
                {
                    subrs.Add(null);
                }

                subrs[index] = Decrypt(encrypted, 4330, lenIV);
                at = next + 1 + length;

                // NP or |, or the longhand "noaccess put" some fonts spell out, ends the entry.
                while (Token(data, at, out int after) is "NP" or "|" or "noaccess" or "put")
                {
                    at = after;
                }
            }
        }

        int charsAt = IndexOf(data, "/CharStrings"u8);
        if (charsAt < 0)
        {
            return;
        }

        int pos = charsAt + 12;
        while (pos < data.Length && charStrings.Count < MaxGlyphs)
        {
            string? token = Token(data, pos, out int next);
            if (token is null || token == "end")
            {
                break;
            }

            if (!token.StartsWith('/'))
            {
                pos = next;
                continue;
            }

            string glyph = token[1..];
            if (!int.TryParse(Token(data, next, out next), NumberStyles.Integer, CultureInfo.InvariantCulture, out int length)
                || Token(data, next, out next) is null || length < 0 || next + 1 + length > data.Length)
            {
                break;
            }

            charStrings.TryAdd(glyph, Decrypt(data.AsSpan(next + 1, length).ToArray(), 4330, lenIV));
            pos = next + 1 + length;

            // ND or |- ends the entry; some converters (ttf2pt1) leave it out and go straight to the next name.
            if (Token(data, pos, out int afterEnd) is string end && !end.StartsWith('/'))
            {
                pos = afterEnd;
            }
        }
    }

    /// <summary>The next whitespace-separated token from <paramref name="at"/>, and where it ends.</summary>
    private static string? Token(byte[] data, int at, out int end)
    {
        while (at < data.Length && data[at] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t')
        {
            at++;
        }

        int start = at;
        while (at < data.Length && data[at] is not ((byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t'))
        {
            at++;
            if (at - start == 1 && data[start] == '/')
            {
                continue;
            }

            if (at < data.Length && data[at] == '/' && at > start)
            {
                break;
            }
        }

        end = at;
        return at > start ? Encoding.Latin1.GetString(data, start, at - start) : null;
    }

    private static byte[] Decrypt(byte[] data, ushort key, int skip)
    {
        if (skip < 0)
        {
            // lenIV -1: the charstrings are not encrypted at all (Type 1 spec, 7.2).
            return data;
        }

        const ushort C1 = 52845, C2 = 22719;
        ushort r = key;
        byte[] plain = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            byte c = data[i];
            plain[i] = (byte)(c ^ (r >> 8));
            r = (ushort)(((c + r) * C1) + C2);
        }

        return skip <= plain.Length ? plain[skip..] : plain;
    }

    private static byte[] FromHex(byte[] hex)
    {
        List<byte> bytes = [];
        int high = -1;
        foreach (byte b in hex)
        {
            int digit = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                _ => -1,
            };
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

        return [.. bytes];
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> text)
    {
        return data.AsSpan().IndexOf(text);
    }

    /// <summary>Writes the CFF: header, name, Top DICT, strings, empty global subroutines, charset, charstrings, Private DICT.</summary>
    private byte[] WriteCff()
    {
        GlyphNames.Add(".notdef");
        GlyphNames.AddRange(charStrings.Keys.Where(n => n != ".notdef"));
        List<byte[]> type2 = [.. GlyphNames.Select(name => charStrings.TryGetValue(name, out byte[]? cs) ? Convert(cs) : [14])];
        List<string> custom = [];
        List<ushort> charset = [];
        foreach (string name in GlyphNames.Skip(1))
        {
            int sid = CffStrings.SidOf(name);
            if (sid < 0)
            {
                sid = CffStrings.StandardCount + custom.Count;
                custom.Add(name);
            }

            charset.Add((ushort)sid);
        }

        byte[] header = [1, 0, 4, 4];
        byte[] nameIndex = Index([Encoding.Latin1.GetBytes(FontName)]);
        byte[] strings = Index([.. custom.Select(Encoding.Latin1.GetBytes)]);
        byte[] globalSubrs = Index([]);
        byte[] charsetBytes = [0, .. charset.SelectMany(s => new[] { (byte)(s >> 8), (byte)s })];
        byte[] charStringIndex = Index(type2);
        byte[] privateDict = [139, 20, 139, 21];

        // The Top DICT's offsets are written as five-byte integers, so its size is known before the offsets are.
        bool matrix = !FontMatrix.SequenceEqual([0.001, 0, 0, 0.001, 0, 0]);
        int topIndexSize = Index([Dict(0, 0, 0, 0, matrix)]).Length;
        int charsetOffset = header.Length + nameIndex.Length + topIndexSize + strings.Length + globalSubrs.Length;
        int charStringsOffset = charsetOffset + charsetBytes.Length;
        int privateOffset = charStringsOffset + charStringIndex.Length;
        byte[] topIndex = Index([Dict(charsetOffset, charStringsOffset, privateDict.Length, privateOffset, matrix)]);
        return [.. header, .. nameIndex, .. topIndex, .. strings, .. globalSubrs, .. charsetBytes, .. charStringIndex, .. privateDict];
    }

    private byte[] Dict(int charset, int charStrings, int privateSize, int privateOffset, bool matrix)
    {
        List<byte> dict = [];
        foreach (double v in FontBBox)
        {
            DictNumber(dict, v);
        }

        dict.Add(5);
        if (matrix)
        {
            foreach (double v in FontMatrix)
            {
                DictNumber(dict, v);
            }

            dict.AddRange([12, 7]);
        }

        DictInt32(dict, charset);
        dict.Add(15);
        DictInt32(dict, charStrings);
        dict.Add(17);
        DictInt32(dict, privateSize);
        DictInt32(dict, privateOffset);
        dict.Add(18);
        return [.. dict];
    }

    private static void DictInt32(List<byte> dict, int value)
    {
        dict.Add(29);
        dict.AddRange([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
    }

    /// <summary>A DICT number: an integer when whole, else a real in nibbles (5176 table 5).</summary>
    private static void DictNumber(List<byte> dict, double value)
    {
        if (value == Math.Round(value) && Math.Abs(value) < 32768)
        {
            dict.Add(28);
            dict.AddRange([(byte)((int)value >> 8), (byte)(int)value]);
            return;
        }

        string text = value.ToString("0.##########E+0", CultureInfo.InvariantCulture).Replace("E+0", string.Empty, StringComparison.Ordinal);
        List<int> nibbles = [];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is >= '0' and <= '9')
            {
                nibbles.Add(c - '0');
            }
            else if (c == '.')
            {
                nibbles.Add(0xA);
            }
            else if (c == '-' && i == 0)
            {
                nibbles.Add(0xE);
            }
            else if (c == 'E')
            {
                bool negative = i + 1 < text.Length && text[i + 1] == '-';
                nibbles.Add(negative ? 0xC : 0xB);
                i += (i + 1 < text.Length && text[i + 1] is '-' or '+') ? 1 : 0;
            }
        }

        nibbles.Add(0xF);
        if (nibbles.Count % 2 == 1)
        {
            nibbles.Add(0xF);
        }

        dict.Add(30);
        for (int i = 0; i < nibbles.Count; i += 2)
        {
            dict.Add((byte)((nibbles[i] << 4) | nibbles[i + 1]));
        }
    }

    private static byte[] Index(List<byte[]> items)
    {
        if (items.Count == 0)
        {
            return [0, 0];
        }

        // count + 1 four-byte offsets, from 1, then the items.
        List<byte> index = [(byte)(items.Count >> 8), (byte)items.Count, 4];
        int offset = 1;
        index.AddRange([0, 0, 0, 1]);
        foreach (byte[] item in items)
        {
            offset += item.Length;
            index.AddRange([(byte)(offset >> 24), (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset]);
        }

        foreach (byte[] item in items)
        {
            index.AddRange(item);
        }

        return [.. index];
    }

    /// <summary>A glyph's Type 1 charstring run into an outline and written as a Type 2 charstring with its width.</summary>
    private byte[] Convert(byte[] charString)
    {
        Outline outline = new();
        try
        {
            Run(charString, outline, 0, 0, 0);
        }
        catch (InvalidDataException)
        {
            // A broken charstring keeps what it drew before the fault.
        }

        return outline.ToType2();
    }

    /// <summary>Runs a charstring, its subroutines and the parts of an accented character, into <paramref name="outline"/>.</summary>
    private void Run(byte[] code, Outline outline, double originX, double originY, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException("Type 1 subroutines nest too deep.");
        }

        Machine m = new(outline, originX, originY);
        Execute(code, m, depth);
    }

    private void Execute(byte[] code, Machine m, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException("Type 1 subroutines nest too deep.");
        }

        List<double> stack = m.Stack;
        int i = 0;
        while (i < code.Length)
        {
            int v = code[i++];
            if (v >= 32)
            {
                if (v <= 246)
                {
                    stack.Add(v - 139);
                }
                else if (v <= 250)
                {
                    stack.Add(((v - 247) * 256) + Byte(code, i++) + 108);
                }
                else if (v <= 254)
                {
                    stack.Add((-(v - 251) * 256) - Byte(code, i++) - 108);
                }
                else
                {
                    if (i + 4 > code.Length)
                    {
                        throw new InvalidDataException("A Type 1 number runs past its charstring.");
                    }

                    stack.Add(BinaryPrimitives.ReadInt32BigEndian(code.AsSpan(i)));
                    i += 4;
                }

                if (stack.Count > 64)
                {
                    throw new InvalidDataException("The Type 1 operand stack overflowed.");
                }

                continue;
            }

            if (v == 12)
            {
                v = 1200 + Byte(code, i++);
            }

            switch (v)
            {
                case 13: // hsbw: sbx wx
                    Need(stack, 2);
                    m.X = m.OriginX + stack[0];
                    m.Y = m.OriginY;
                    m.Outline.SetWidth(stack[1], m.IsAccent);
                    m.Sbx = stack[0];
                    stack.Clear();
                    break;
                case 1207: // sbw: sbx sby wx wy
                    Need(stack, 4);
                    m.X = m.OriginX + stack[0];
                    m.Y = m.OriginY + stack[1];
                    m.Outline.SetWidth(stack[2], m.IsAccent);
                    m.Sbx = stack[0];
                    stack.Clear();
                    break;
                case 21: // rmoveto
                    Need(stack, 2);
                    Move(m, stack[^2], stack[^1]);
                    stack.Clear();
                    break;
                case 22: // hmoveto
                    Need(stack, 1);
                    Move(m, stack[^1], 0);
                    stack.Clear();
                    break;
                case 4: // vmoveto
                    Need(stack, 1);
                    Move(m, 0, stack[^1]);
                    stack.Clear();
                    break;
                case 5: // rlineto
                    Need(stack, 2);
                    m.X += stack[^2];
                    m.Y += stack[^1];
                    m.Outline.LineTo(m.X, m.Y);
                    stack.Clear();
                    break;
                case 6: // hlineto
                    Need(stack, 1);
                    m.X += stack[^1];
                    m.Outline.LineTo(m.X, m.Y);
                    stack.Clear();
                    break;
                case 7: // vlineto
                    Need(stack, 1);
                    m.Y += stack[^1];
                    m.Outline.LineTo(m.X, m.Y);
                    stack.Clear();
                    break;
                case 8: // rrcurveto
                    Need(stack, 6);
                    Curve(m, stack[^6], stack[^5], stack[^4], stack[^3], stack[^2], stack[^1]);
                    stack.Clear();
                    break;
                case 30: // vhcurveto: dy1 dx2 dy2 dx3
                    Need(stack, 4);
                    Curve(m, 0, stack[^4], stack[^3], stack[^2], stack[^1], 0);
                    stack.Clear();
                    break;
                case 31: // hvcurveto: dx1 dx2 dy2 dy3
                    Need(stack, 4);
                    Curve(m, stack[^4], 0, stack[^3], stack[^2], 0, stack[^1]);
                    stack.Clear();
                    break;
                case 9: // closepath: Type 2 closes contours itself.
                    stack.Clear();
                    break;
                case 10: // callsubr
                    Need(stack, 1);
                    int index = (int)stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    if ((uint)index < (uint)subrs.Count && subrs[index] is byte[] subr)
                    {
                        Execute(subr, m, depth + 1);
                    }

                    break;
                case 11: // return
                    return;
                case 14: // endchar
                    m.Ended = true;
                    return;
                case 1206: // seac: asb adx ady bchar achar
                    Need(stack, 5);
                    Seac(m, stack[^5], stack[^4], stack[^3], (int)stack[^2], (int)stack[^1], depth);
                    m.Ended = true;
                    return;
                case 1216: // callothersubr: args... n othersubr#
                    Need(stack, 2);
                    OtherSubr(m, stack);
                    break;
                case 1217: // pop: a result of the last othersubr
                    stack.Add(m.PostScript.Count > 0 ? m.PostScript.Pop() : 0);
                    break;
                case 1233: // setcurrentpoint
                    Need(stack, 2);
                    m.X = m.OriginX + stack[^2];
                    m.Y = m.OriginY + stack[^1];
                    stack.Clear();
                    break;
                case 1212: // div
                    Need(stack, 2);
                    double b = stack[^1], a = stack[^2];
                    stack.RemoveRange(stack.Count - 2, 2);
                    stack.Add(b == 0 ? 0 : a / b);
                    break;
                case 1: // hstem
                case 3: // vstem
                case 1200: // dotsection
                case 1201: // vstem3
                case 1202: // hstem3
                    stack.Clear();
                    break;
                default:
                    stack.Clear();
                    break;
            }

            if (m.Ended)
            {
                return;
            }
        }
    }

    private static void Move(Machine m, double dx, double dy)
    {
        m.X += dx;
        m.Y += dy;
        if (m.InFlex)
        {
            // Flex reference and control points arrive as moves (othersubr 2) and are drawn when othersubr 0 ends the flex.
            m.FlexPoints.Add((m.X, m.Y));
            return;
        }

        m.Outline.MoveTo(m.X, m.Y);
    }

    private static void Curve(Machine m, double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
    {
        double x1 = m.X + dx1, y1 = m.Y + dy1;
        double x2 = x1 + dx2, y2 = y1 + dy2;
        m.X = x2 + dx3;
        m.Y = y2 + dy3;
        m.Outline.CurveTo(x1, y1, x2, y2, m.X, m.Y);
    }

    /// <summary>The othersubrs of the Type 1 spec's section 8: flex (0, 1, 2) and hint replacement (3); others are passed over.</summary>
    private static void OtherSubr(Machine m, List<double> stack)
    {
        int number = (int)stack[^1];
        int count = (int)stack[^2];
        stack.RemoveRange(stack.Count - 2, 2);
        count = Math.Clamp(count, 0, stack.Count);
        double[] args = [.. stack.GetRange(stack.Count - count, count)];
        stack.RemoveRange(stack.Count - count, count);
        switch (number)
        {
            case 1:
                m.InFlex = true;
                m.FlexPoints.Clear();
                break;
            case 2:
                break;
            case 0:
                m.InFlex = false;
                // The reference point and six control points; the curves join at the fourth.
                List<(double X, double Y)> p = m.FlexPoints;
                if (p.Count >= 7)
                {
                    m.Outline.CurveTo(p[1].X, p[1].Y, p[2].X, p[2].Y, p[3].X, p[3].Y);
                    m.Outline.CurveTo(p[4].X, p[4].Y, p[5].X, p[5].Y, p[6].X, p[6].Y);
                    m.X = p[6].X;
                    m.Y = p[6].Y;
                }

                // The two pops after it give the end point to setcurrentpoint: x first, then y.
                m.PostScript.Clear();
                m.PostScript.Push(m.Y - m.OriginY);
                m.PostScript.Push(m.X - m.OriginX);
                break;
            case 3:
                // Hint replacement: the pop that follows gives subroutine 3 back to callsubr, which only sets hints.
                m.PostScript.Clear();
                m.PostScript.Push(3);
                break;
            default:
                m.PostScript.Clear();
                for (int i = args.Length - 1; i >= 0; i--)
                {
                    m.PostScript.Push(args[i]);
                }

                break;
        }
    }

    /// <summary>An accented character: the base and the accent from StandardEncoding, the accent moved by (adx, ady).</summary>
    private void Seac(Machine m, double asb, double adx, double ady, int baseCode, int accentCode, int depth)
    {
        string?[] standard = FontEncodings.Standard;
        string? baseName = (uint)baseCode < 256 ? standard[baseCode] : null;
        string? accentName = (uint)accentCode < 256 ? standard[accentCode] : null;
        if (baseName is not null && charStrings.TryGetValue(baseName, out byte[]? baseCs))
        {
            Machine b = new(m.Outline, m.OriginX, m.OriginY) { IsAccent = true };
            Execute(baseCs, b, depth + 1);
        }

        if (accentName is not null && charStrings.TryGetValue(accentName, out byte[]? accentCs))
        {
            // As Type 2's endchar puts it: the accent's origin at (adx + sbx - asb, ady) from the base's origin.
            Machine a = new(m.Outline, m.OriginX + adx + m.Sbx - asb, m.OriginY + ady) { IsAccent = true };
            Execute(accentCs, a, depth + 1);
        }
    }

    private static int Byte(byte[] code, int i)
    {
        return i < code.Length ? code[i] : throw new InvalidDataException("A Type 1 operand runs past its charstring.");
    }

    private static void Need(List<double> stack, int count)
    {
        if (stack.Count < count)
        {
            throw new InvalidDataException("A Type 1 operator has too few operands.");
        }
    }

    /// <summary>The running state of one charstring: its point, stacks, flex and where its origin sits.</summary>
    private sealed class Machine(Outline outline, double originX, double originY)
    {
        public Outline Outline { get; } = outline;

        public double OriginX { get; } = originX;

        public double OriginY { get; } = originY;

        public double X { get; set; } = originX;

        public double Y { get; set; } = originY;

        public double Sbx { get; set; }

        public bool IsAccent { get; init; }

        public bool Ended { get; set; }

        public bool InFlex { get; set; }

        public List<double> Stack { get; } = [];

        public Stack<double> PostScript { get; } = new();

        public List<(double X, double Y)> FlexPoints { get; } = [];
    }

    /// <summary>An outline written as a Type 2 charstring: width, then moves, lines and curves relative to the last point written.</summary>
    private sealed class Outline
    {
        private readonly List<byte> code = [];
        private double? width;
        private double x;
        private double y;
        private bool started;

        public void SetWidth(double value, bool accent)
        {
            if (!accent || width is null)
            {
                width ??= value;
            }
        }

        public void MoveTo(double toX, double toY)
        {
            Begin();
            Write(toX - x, toY - y, 21);
            (x, y) = (Rounded(toX), Rounded(toY));
        }

        public void LineTo(double toX, double toY)
        {
            Begin();
            Write(toX - x, toY - y, 5);
            (x, y) = (Rounded(toX), Rounded(toY));
        }

        public void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
        {
            Begin();
            double rx1 = Rounded(x1), ry1 = Rounded(y1), rx2 = Rounded(x2), ry2 = Rounded(y2);
            Number(rx1 - x);
            Number(ry1 - y);
            Number(rx2 - rx1);
            Number(ry2 - ry1);
            Number(Rounded(x3) - rx2);
            Number(Rounded(y3) - ry2);
            code.Add(8);
            (x, y) = (Rounded(x3), Rounded(y3));
        }

        public byte[] ToType2()
        {
            if (!started)
            {
                Number(width ?? 0);
            }

            code.Add(14);
            return [.. code];
        }

        private void Begin()
        {
            if (!started)
            {
                started = true;
                Number(width ?? 0);
            }
        }

        private void Write(double dx, double dy, byte op)
        {
            Number(Rounded(dx + x) - x);
            Number(Rounded(dy + y) - y);
            code.Add(op);
        }

        /// <summary>Points kept to 1/65536, so relative moves add up to where the outline is without drift.</summary>
        private static double Rounded(double value)
        {
            return Math.Round(value * 65536) / 65536;
        }

        private void Number(double value)
        {
            if (value == Math.Round(value) && Math.Abs(value) <= 32767)
            {
                int v = (int)value;
                if (v is >= -107 and <= 107)
                {
                    code.Add((byte)(v + 139));
                }
                else if (v is >= 108 and <= 1131)
                {
                    v -= 108;
                    code.Add((byte)((v >> 8) + 247));
                    code.Add((byte)v);
                }
                else if (v is >= -1131 and <= -108)
                {
                    v = -v - 108;
                    code.Add((byte)((v >> 8) + 251));
                    code.Add((byte)v);
                }
                else
                {
                    code.Add(28);
                    code.Add((byte)(v >> 8));
                    code.Add((byte)v);
                }

                return;
            }

            int fixedValue = (int)Math.Round(Math.Clamp(value, -32768, 32767) * 65536);
            code.Add(255);
            code.AddRange([(byte)(fixedValue >> 24), (byte)(fixedValue >> 16), (byte)(fixedValue >> 8), (byte)fixedValue]);
        }
    }
}
