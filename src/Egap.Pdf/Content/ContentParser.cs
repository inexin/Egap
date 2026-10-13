using System.Globalization;
using System.Text;

using Egap.Pdf.IO;
using Egap.Pdf.Parsing;

namespace Egap.Pdf.Content;

/// <summary>
/// Reads content stream bytes into operations (8.2, Phase 3 design Q3). It works on the bytes directly, since content is
/// already in memory and is read far more often than file structure. It is lenient the way viewers are: unknown operators are
/// kept, a wrong operand count is kept with a warning, stray bytes are skipped with a warning, and an array or dictionary left
/// open is closed at the next operator. Only nesting deeper than <see cref="PdfReadOptions.MaxNestingDepth"/> throws.
/// </summary>
internal sealed class ContentParser
{
    /// <summary>How many bytes after a candidate <c>EI</c> must look like content for the search to accept it.</summary>
    private const int EndCheckLength = 64;

    private static readonly PdfInteger[] SmallIntegers = [.. Enumerable.Range(-1, 1026).Select(i => new PdfInteger(i))];

    private readonly byte[] data;
    private readonly int end;
    private readonly PdfReadOptions options;
    private readonly WarningLog log;
    private readonly List<PdfObject> operands = new(8);
    private readonly Stack<Container> containers = new();
    private int position;

    public ContentParser(byte[] data, int length, PdfReadOptions options, WarningLog log)
    {
        this.data = data;
        end = length;
        this.options = options;
        this.log = log;
    }

    /// <summary>The offset where the last operation's first token started.</summary>
    public int OperationStart { get; private set; }

    /// <summary>The offset after the last operation's operator.</summary>
    public int Position => position;

    /// <summary>
    /// Whether the content ended inside something left open (a string, an array, an inline image, operands waiting for
    /// their operator), so that content appended to it would be read as part of that.
    /// </summary>
    public bool EndedOpen { get; private set; }

    /// <summary>Reads the next operation, or <see langword="null"/> at the end of the data.</summary>
    public PdfOperation? Next()
    {
        operands.Clear();
        OperationStart = -1;
        while (true)
        {
            SkipWhiteSpaceAndComments();
            if (position >= end)
            {
                if (containers.Count > 0)
                {
                    EndedOpen = true;
                    Warn("An array or dictionary is not closed at the end of the content.");
                    CloseContainers();
                }

                if (operands.Count > 0)
                {
                    EndedOpen = true;
                    Warn($"{operands.Count} operand(s) at the end of the content have no operator; they were dropped.");
                    operands.Clear();
                }

                return null;
            }

            int start = position;
            if (OperationStart < 0)
            {
                OperationStart = start;
            }

            byte b = data[position];
            PdfObject? value;
            switch (b)
            {
                case (byte)'/':
                    position++;
                    value = ReadName();
                    break;
                case (byte)'(':
                    position++;
                    value = ReadLiteralString();
                    break;
                case (byte)'<':
                    if (Peek(1) == '<')
                    {
                        position += 2;
                        Open(new PdfDictionary(), start);
                        continue;
                    }

                    position++;
                    value = ReadHexString();
                    break;
                case (byte)'>':
                    if (Peek(1) == '>')
                    {
                        position += 2;
                        if (containers.TryPeek(out Container? top) && top.Value is PdfDictionary)
                        {
                            if (top.Key is not null)
                            {
                                Warn($"The key /{top.Key.Value} has no value; it was dropped.", start);
                            }

                            value = containers.Pop().Value;
                            break;
                        }

                        Warn("A '>>' outside a dictionary was skipped.", start);
                        continue;
                    }

                    position++;
                    Warn("A single '>' was skipped.", start);
                    continue;
                case (byte)'[':
                    position++;
                    Open(new PdfArray(), start);
                    continue;
                case (byte)']':
                    position++;
                    if (containers.TryPeek(out Container? array) && array.Value is PdfArray)
                    {
                        value = containers.Pop().Value;
                        break;
                    }

                    Warn("A ']' outside an array was skipped.", start);
                    continue;
                case (byte)')' or (byte)'{' or (byte)'}':
                    position++;
                    Warn($"A stray '{(char)b}' was skipped.", start);
                    continue;
                default:
                    if (CharClass.IsDigit(b) || b is (byte)'+' or (byte)'-' or (byte)'.')
                    {
                        value = ReadNumber();
                        break;
                    }

                    while (position < end && CharClass.IsRegular(data[position]))
                    {
                        position++;
                    }

                    ReadOnlySpan<byte> keyword = data.AsSpan(start, position - start);
                    if (keyword.SequenceEqual("true"u8))
                    {
                        value = PdfBoolean.True;
                        break;
                    }

                    if (keyword.SequenceEqual("false"u8))
                    {
                        value = PdfBoolean.False;
                        break;
                    }

                    if (keyword.SequenceEqual("null"u8))
                    {
                        value = PdfNull.Instance;
                        break;
                    }

                    if (containers.Count > 0)
                    {
                        Warn("An array or dictionary is not closed before an operator; it was closed there.", start);
                        CloseContainers();
                    }

                    return Operation(keyword, start);
            }

            Deliver(value, start);
        }
    }

    private PdfOperation Operation(ReadOnlySpan<byte> keyword, int start)
    {
        if (!Operators.TryGet(keyword, out string name, out int expected))
        {
            return new PdfOperation(Encoding.Latin1.GetString(keyword), [.. operands], trusted: true);
        }

        if (name == "BI")
        {
            if (operands.Count > 0)
            {
                Warn($"{operands.Count} operand(s) before BI were dropped.", start);
            }

            return ReadInlineImage(start);
        }

        if (expected != Operators.Variable && expected != operands.Count)
        {
            Warn($"'{name}' has {operands.Count} operand(s) where it takes {expected}.", start, PdfWarningCode.WrongOperandCount);
        }

        return new PdfOperation(name, operands.Count == 0 ? [] : [.. operands], trusted: true);
    }

    /// <summary>Reads an inline image's parameters, data and <c>EI</c> after <c>BI</c> (8.9.7).</summary>
    private PdfInlineImage ReadInlineImage(int start)
    {
        PdfDictionary parameters = [];
        while (true)
        {
            SkipWhiteSpaceAndComments();
            if (position >= end)
            {
                EndedOpen = true;
                Warn("An inline image has no ID; it was dropped.", start, PdfWarningCode.MalformedInlineImage);
                return new PdfInlineImage(parameters, []);
            }

            if (data[position] == 'I' && Peek(1) == 'D' && !CharClass.IsRegular(Peek(2)))
            {
                position += 2;
                break;
            }

            if (data[position] != '/')
            {
                // Something other than a key: read it as an object and drop it, as viewers do.
                int skipped = position;
                ParseValue();
                Warn("An inline image parameter that is not a name was skipped.", skipped, PdfWarningCode.MalformedInlineImage);
                continue;
            }

            position++;
            PdfName key = ReadName();
            SkipWhiteSpaceAndComments();
            if (position < end && data[position] == 'I' && Peek(1) == 'D' && !CharClass.IsRegular(Peek(2)))
            {
                Warn($"The inline image key /{key.Value} has no value.", position, PdfWarningCode.MalformedInlineImage);
                continue;
            }

            parameters[key] = ParseValue();
        }

        // One white-space byte separates ID from the data (8.9.7).
        if (position < end && CharClass.IsWhiteSpace(data[position]))
        {
            position++;
        }

        int dataStart = position;
        int dataEnd = FindInlineImageEnd(parameters, dataStart, start);
        byte[] bytes = data.AsSpan(dataStart, dataEnd - dataStart).ToArray();
        return new PdfInlineImage(parameters, bytes);
    }

    /// <summary>
    /// Finds where an inline image's data ends and moves past its <c>EI</c>: by its <c>/L</c> or <c>/Length</c>, else by the
    /// size of unfiltered samples, else by searching for an <c>EI</c> between white space that is followed by content.
    /// </summary>
    private int FindInlineImageEnd(PdfDictionary parameters, int dataStart, int start)
    {
        long? known = ExpectedLength(parameters);
        if (known is long length && length >= 0 && length <= end - dataStart && IsEndAt((int)(dataStart + length), out int after))
        {
            position = after;
            return (int)(dataStart + length);
        }

        for (int i = dataStart; i + 1 < end; i++)
        {
            if (data[i] == 'E' && data[i + 1] == 'I' && (i == dataStart || CharClass.IsWhiteSpace(data[i - 1]))
                && (i + 2 == end || !CharClass.IsRegular(data[i + 2])) && LooksLikeContent(i + 2))
            {
                if (known is not null)
                {
                    Warn("An inline image's data is not as long as its parameters say; its end was found by searching.", start, PdfWarningCode.MalformedInlineImage);
                }

                position = i + 2;
                int dataEnd = i > dataStart ? i - 1 : i;
                if (dataEnd > dataStart && data[dataEnd] == '\n' && data[dataEnd - 1] == '\r')
                {
                    dataEnd--;
                }

                return dataEnd;
            }
        }

        EndedOpen = true;
        Warn("An inline image has no EI; its data runs to the end of the content.", start, PdfWarningCode.MalformedInlineImage);
        position = end;
        return end;
    }

    /// <summary>Whether white space and then <c>EI</c> follow <paramref name="offset"/>, and where after <c>EI</c> reading goes on.</summary>
    private bool IsEndAt(int offset, out int after)
    {
        int i = offset;
        while (i < end && CharClass.IsWhiteSpace(data[i]))
        {
            i++;
        }

        after = i + 2;
        return i + 1 < end && data[i] == 'E' && data[i + 1] == 'I' && (i + 2 == end || !CharClass.IsRegular(data[i + 2]));
    }

    /// <summary>
    /// Whether the bytes from <paramref name="offset"/> look like content rather than more image data: text up to the first
    /// operator the spec defines, or for the next 64 bytes. Literal strings are skipped whole, since they may hold any bytes.
    /// </summary>
    private bool LooksLikeContent(int offset)
    {
        int stop = Math.Min(end, offset + EndCheckLength);
        int i = offset;
        while (i < stop)
        {
            byte b = data[i];
            if (b == '(')
            {
                i = SkipLiteralString(i + 1) + 1;
                continue;
            }

            if (b is > 0x7E || (b < 0x20 && !CharClass.IsWhiteSpace(b)))
            {
                return false;
            }

            if (!CharClass.IsRegular(b) || b == '/')
            {
                i++;
                if (b == '/')
                {
                    while (i < end && CharClass.IsRegular(data[i]))
                    {
                        i++;
                    }
                }

                continue;
            }

            int word = i;
            while (i < end && CharClass.IsRegular(data[i]))
            {
                i++;
            }

            if (Operators.TryGet(data.AsSpan(word, i - word), out _, out _))
            {
                return true;
            }
        }

        return true;
    }

    /// <summary>The offset of the <c>)</c> that closes a literal string starting at <paramref name="offset"/>, or the end.</summary>
    private int SkipLiteralString(int offset)
    {
        int depth = 1;
        for (int i = offset; i < end; i++)
        {
            switch (data[i])
            {
                case (byte)'\\':
                    i++;
                    break;
                case (byte)'(':
                    depth++;
                    break;
                case (byte)')' when --depth == 0:
                    return i;
            }
        }

        return end;
    }

    /// <summary>
    /// The data length an inline image's parameters give: its <c>/L</c> or <c>/Length</c>, else the size of unfiltered
    /// samples in a device colour space; <see langword="null"/> when they give none.
    /// </summary>
    internal static long? ExpectedLength(PdfDictionary parameters)
    {
        return parameters.GetInt("L") ?? parameters.GetInt("Length") ?? UnfilteredLength(parameters);
    }

    /// <summary>The data length of an unfiltered inline image with a device colour space, or <see langword="null"/>.</summary>
    private static long? UnfilteredLength(PdfDictionary parameters)
    {
        if ((parameters.Get("F") ?? parameters.Get("Filter")) is PdfName or PdfArray { Count: > 0 })
        {
            return null;
        }

        long? width = parameters.GetInt("W") ?? parameters.GetInt("Width");
        long? height = parameters.GetInt("H") ?? parameters.GetInt("Height");
        bool mask = (parameters.GetBool("IM") ?? parameters.GetBool("ImageMask")) == true;
        long bits = mask ? 1 : parameters.GetInt("BPC") ?? parameters.GetInt("BitsPerComponent") ?? 8;
        int? components = mask ? 1 : (parameters.Get("CS") ?? parameters.Get("ColorSpace")) switch
        {
            PdfName { Value: "G" or "DeviceGray" or "I" or "Indexed" } => 1,
            PdfName { Value: "RGB" or "DeviceRGB" } => 3,
            PdfName { Value: "CMYK" or "DeviceCMYK" } => 4,
            PdfArray { Count: > 0 } array when array.GetName(0)?.Value is "I" or "Indexed" => 1,
            _ => null,
        };
        if (width is not (> 0 and < 1 << 20) || height is not (> 0 and < 1 << 20) || bits is not (1 or 2 or 4 or 8 or 16) || components is not int c)
        {
            return null;
        }

        return (((width.Value * c * bits) + 7) / 8) * height.Value;
    }

    /// <summary>Reads one whole operand (with any arrays and dictionaries in it) at the current position.</summary>
    private PdfObject ParseValue()
    {
        int depth = containers.Count;
        while (true)
        {
            SkipWhiteSpaceAndComments();
            if (position >= end)
            {
                return CloseTo(depth) ?? PdfNull.Instance;
            }

            int start = position;
            byte b = data[position];
            PdfObject? value;
            switch (b)
            {
                case (byte)'/':
                    position++;
                    value = ReadName();
                    break;
                case (byte)'(':
                    position++;
                    value = ReadLiteralString();
                    break;
                case (byte)'<' when Peek(1) == '<':
                    position += 2;
                    Open(new PdfDictionary(), start);
                    continue;
                case (byte)'<':
                    position++;
                    value = ReadHexString();
                    break;
                case (byte)'>' when Peek(1) == '>' && containers.Count > depth && containers.Peek().Value is PdfDictionary:
                    position += 2;
                    value = containers.Pop().Value;
                    break;
                case (byte)'[':
                    position++;
                    Open(new PdfArray(), start);
                    continue;
                case (byte)']' when containers.Count > depth && containers.Peek().Value is PdfArray:
                    position++;
                    value = containers.Pop().Value;
                    break;
                default:
                    if (CharClass.IsDigit(b) || b is (byte)'+' or (byte)'-' or (byte)'.')
                    {
                        value = ReadNumber();
                        break;
                    }

                    if (!CharClass.IsRegular(b))
                    {
                        position++;
                        continue;
                    }

                    int keywordStart = position;
                    while (position < end && CharClass.IsRegular(data[position]))
                    {
                        position++;
                    }

                    ReadOnlySpan<byte> keyword = data.AsSpan(keywordStart, position - keywordStart);
                    value = keyword.SequenceEqual("true"u8) ? PdfBoolean.True : keyword.SequenceEqual("false"u8) ? PdfBoolean.False : PdfNull.Instance;
                    if (keyword.SequenceEqual("ID"u8))
                    {
                        // The end of the parameters: leave ID to the caller.
                        position = keywordStart;
                        return CloseTo(depth) ?? PdfNull.Instance;
                    }

                    break;
            }

            if (containers.Count == depth)
            {
                return value;
            }

            Deliver(value, start);
        }
    }

    /// <summary>Closes containers down to <paramref name="depth"/>, returning the outermost one closed.</summary>
    private PdfObject? CloseTo(int depth)
    {
        PdfObject? closed = null;
        while (containers.Count > depth)
        {
            closed = containers.Pop().Value;
            if (containers.Count > depth)
            {
                Deliver(closed, position);
            }
        }

        return closed;
    }

    private void Open(PdfObject container, int start)
    {
        if (containers.Count >= options.MaxNestingDepth)
        {
            throw new PdfLimitException(nameof(PdfReadOptions.MaxNestingDepth), $"Content operands nest deeper than {options.MaxNestingDepth} levels at offset {start}.");
        }

        containers.Push(new Container(container));
    }

    private void CloseContainers()
    {
        PdfObject? value = CloseTo(0);
        if (value is not null)
        {
            operands.Add(value);
        }
    }

    /// <summary>Puts a finished value into the open container, or into the operands when none is open.</summary>
    private void Deliver(PdfObject value, int start)
    {
        if (!containers.TryPeek(out Container? top))
        {
            operands.Add(value);
            return;
        }

        if (top.Value is PdfArray array)
        {
            array.Add(value);
            return;
        }

        PdfDictionary dictionary = (PdfDictionary)top.Value;
        if (top.Key is null)
        {
            if (value is PdfName key)
            {
                top.Key = key;
            }
            else
            {
                Warn("A dictionary key is not a name; it was skipped.", start);
            }

            return;
        }

        dictionary[top.Key] = value;
        top.Key = null;
    }

    private PdfObject ReadNumber()
    {
        int start = position;
        bool negative = false;
        int signs = 0;
        while (position < end && data[position] is (byte)'+' or (byte)'-')
        {
            negative |= data[position] == '-';
            signs++;
            position++;
        }

        long mantissa = 0;
        int digits = 0;
        int fraction = -1;
        bool malformed = signs > 1;
        bool overflow = false;
        while (position < end)
        {
            byte b = data[position];
            if (CharClass.IsDigit(b))
            {
                if (mantissa < 100_000_000_000_000_000)
                {
                    mantissa = (mantissa * 10) + (b - '0');
                    if (fraction >= 0)
                    {
                        fraction++;
                    }
                }
                else if (fraction < 0)
                {
                    overflow = true;
                }

                digits++;
            }
            else if (b == '.' && fraction < 0)
            {
                fraction = 0;
            }
            else if (b == '-' && digits > 0)
            {
                // A minus inside a number, as some producers write: ignored, as Adobe Reader and pdf.js do.
                malformed = true;
            }
            else
            {
                break;
            }

            position++;
        }

        if (digits == 0)
        {
            Warn($"'{Encoding.ASCII.GetString(data, start, position - start)}' is not a number; read as 0.", start);
            return SmallIntegers[1];
        }

        if (malformed)
        {
            Warn($"Malformed number '{Encoding.ASCII.GetString(data, start, position - start)}'.", start);
        }

        if (fraction < 0 && !overflow)
        {
            long value = negative ? -mantissa : mantissa;
            return value is >= -1 and <= 1024 ? SmallIntegers[value + 1] : new PdfInteger(value);
        }

        if (fraction < 0 && long.TryParse(Digits(start), NumberStyles.None, CultureInfo.InvariantCulture, out long large))
        {
            // Too long for the fast path but still a 64-bit integer.
            return new PdfInteger(negative ? -large : large);
        }

        double real;
        if (!overflow && fraction <= 15 && mantissa < 1L << 53)
        {
            // Both are exact doubles, so one division rounds correctly, as parsing the text would.
            real = mantissa / Pow10[fraction < 0 ? 0 : fraction];
        }
        else
        {
            real = ParseSlow(start);
        }

        return new PdfReal(negative ? -real : real);
    }

    private static readonly double[] Pow10 = [1, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15];

    /// <summary>A number's magnitude from its digits and point alone, for numbers too long for the fast path.</summary>
    private double ParseSlow(int start)
    {
        double value = double.Parse(Digits(start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        return double.IsFinite(value) ? value : 0;
    }

    /// <summary>The digits and point of the number from <paramref name="start"/> to the current position, signs left out.</summary>
    private string Digits(int start)
    {
        StringBuilder text = new(position - start);
        for (int i = start; i < position; i++)
        {
            if (CharClass.IsDigit(data[i]) || data[i] == '.')
            {
                text.Append((char)data[i]);
            }
        }

        return text.ToString();
    }

    private PdfName ReadName()
    {
        int start = position;
        while (position < end && CharClass.IsRegular(data[position]))
        {
            if (data[position] == '#')
            {
                return ReadEscapedName(start);
            }

            position++;
        }

        return PdfName.Get(data.AsSpan(start, position - start));
    }

    private PdfName ReadEscapedName(int start)
    {
        position = start;
        List<byte> bytes = [];
        while (position < end && CharClass.IsRegular(data[position]))
        {
            byte b = data[position++];
            if (b == '#' && position + 1 < end && CharClass.HexValue(data[position]) is int high and >= 0 && CharClass.HexValue(data[position + 1]) is int low and >= 0)
            {
                b = (byte)((high << 4) | low);
                position += 2;
            }

            bytes.Add(b);
        }

        return PdfName.Get(bytes.ToArray());
    }

    private PdfString ReadLiteralString()
    {
        int start = position;
        int depth = 1;

        // Most strings have no escapes or line ends to translate: take their bytes as they are.
        int scan = position;
        while (scan < end)
        {
            byte b = data[scan];
            if (b is (byte)'\\' or (byte)'\r')
            {
                break;
            }

            if (b == '(')
            {
                depth++;
            }
            else if (b == ')' && --depth == 0)
            {
                position = scan + 1;
                return PdfString.FromParsed(data.AsSpan(start, scan - start).ToArray(), hex: false);
            }

            scan++;
        }

        depth = 1;
        List<byte> bytes = new(Math.Max(16, scan - start + 16));
        while (position < end)
        {
            byte b = data[position++];
            switch (b)
            {
                case (byte)'(':
                    depth++;
                    break;
                case (byte)')':
                    if (--depth == 0)
                    {
                        return PdfString.FromParsed([.. bytes], hex: false);
                    }

                    break;
                case (byte)'\r':
                    if (position < end && data[position] == '\n')
                    {
                        position++;
                    }

                    b = (byte)'\n';
                    break;
                case (byte)'\\':
                    if (position >= end)
                    {
                        continue;
                    }

                    b = data[position++];
                    switch (b)
                    {
                        case (byte)'n':
                            b = (byte)'\n';
                            break;
                        case (byte)'r':
                            b = (byte)'\r';
                            break;
                        case (byte)'t':
                            b = (byte)'\t';
                            break;
                        case (byte)'b':
                            b = (byte)'\b';
                            break;
                        case (byte)'f':
                            b = (byte)'\f';
                            break;
                        case (byte)'\r':
                            if (position < end && data[position] == '\n')
                            {
                                position++;
                            }

                            continue;
                        case (byte)'\n':
                            continue;
                        case >= (byte)'0' and <= (byte)'7':
                            int value = b - '0';
                            for (int i = 0; i < 2 && position < end && data[position] is >= (byte)'0' and <= (byte)'7'; i++)
                            {
                                value = (value * 8) + (data[position++] - '0');
                            }

                            b = (byte)value;
                            break;
                    }

                    break;
            }

            bytes.Add(b);
        }

        EndedOpen = true;
        Warn("A string has no closing ')'; it was read to the end of the content.", start - 1);
        return PdfString.FromParsed([.. bytes], hex: false);
    }

    private PdfString ReadHexString()
    {
        int start = position;
        List<byte> bytes = [];
        int high = -1;
        while (position < end)
        {
            byte b = data[position++];
            if (b == '>')
            {
                if (high >= 0)
                {
                    bytes.Add((byte)(high << 4));
                }

                return PdfString.FromParsed([.. bytes], hex: true);
            }

            int value = CharClass.HexValue(b);
            if (value < 0)
            {
                continue;
            }

            if (high < 0)
            {
                high = value;
            }
            else
            {
                bytes.Add((byte)((high << 4) | value));
                high = -1;
            }
        }

        EndedOpen = true;
        Warn("A hex string has no closing '>'; it was read to the end of the content.", start - 1);
        if (high >= 0)
        {
            bytes.Add((byte)(high << 4));
        }

        return PdfString.FromParsed([.. bytes], hex: true);
    }

    private void SkipWhiteSpaceAndComments()
    {
        while (position < end)
        {
            byte b = data[position];
            if (CharClass.IsWhiteSpace(b))
            {
                position++;
            }
            else if (b == '%')
            {
                while (position < end && data[position] is not ((byte)'\r' or (byte)'\n'))
                {
                    position++;
                }
            }
            else
            {
                return;
            }
        }
    }

    private int Peek(int offset)
    {
        return position + offset < end ? data[position + offset] : -1;
    }

    private void Warn(string message, int? offset = null, PdfWarningCode code = PdfWarningCode.MalformedContent)
    {
        log.Add(code, message, offset ?? position);
    }

    private sealed class Container(PdfObject value)
    {
        public PdfObject Value { get; } = value;

        public PdfName? Key { get; set; }
    }
}
