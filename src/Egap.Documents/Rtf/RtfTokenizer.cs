namespace Egap.Documents.Rtf;

/// <summary>What <see cref="RtfTokenizer.Next"/> found.</summary>
internal enum RtfTokenKind
{
    /// <summary>The end of the file.</summary>
    End,

    /// <summary>An opening brace.</summary>
    GroupStart,

    /// <summary>A closing brace.</summary>
    GroupEnd,

    /// <summary>A control word, such as <c>\b</c> or <c>\fs24</c>: <see cref="RtfTokenizer.Word"/> and its parameter.</summary>
    ControlWord,

    /// <summary>A control symbol other than <c>\'</c>, such as <c>\~</c> or <c>\*</c>: <see cref="RtfTokenizer.Symbol"/>.</summary>
    ControlSymbol,

    /// <summary>A byte written as <c>\'hh</c>: <see cref="RtfTokenizer.Byte"/>.</summary>
    Hex,

    /// <summary>Plain text bytes, without the line breaks RTF ignores: <see cref="RtfTokenizer.Text"/>.</summary>
    Text,
}

/// <summary>
/// Splits RTF into tokens (RTF 1.9.1, "RTF syntax"): groups, control words with their optional signed parameter, control
/// symbols, <c>\'hh</c> bytes and runs of text. Carriage returns and line feeds outside control words are skipped, as RTF
/// ignores them; a backslash before one is a paragraph mark, reported as the control word <c>par</c>.
/// </summary>
internal sealed class RtfTokenizer
{
    // Interning keeps the reader from allocating a string for every control word; hostile files with endless new names stop
    // being interned at this size.
    private const int MaxInterned = 4096;

    private readonly byte[] data;
    private readonly Dictionary<string, string> interned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> lookup;
    private int position;
    private int textStart;
    private int textLength;

    public RtfTokenizer(byte[] data)
    {
        this.data = data;
        lookup = interned.GetAlternateLookup<ReadOnlySpan<char>>();

        // A UTF-8 byte order mark and white space before the first brace, which Word lets through.
        if (data is [0xEF, 0xBB, 0xBF, ..])
        {
            position = 3;
        }

        while (position < data.Length && data[position] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            position++;
        }
    }

    /// <summary>Where the next token starts, as an offset in the file.</summary>
    public int Position => position;

    /// <summary>The file's bytes.</summary>
    public byte[] Data => data;

    /// <summary>The control word's name, for <see cref="RtfTokenKind.ControlWord"/>.</summary>
    public string Word { get; private set; } = "";

    /// <summary>Whether the control word has a parameter.</summary>
    public bool HasParameter { get; private set; }

    /// <summary>The control word's parameter, clamped to <see cref="int"/>; 0 when it has none.</summary>
    public int Parameter { get; private set; }

    /// <summary>The control symbol's character.</summary>
    public char Symbol { get; private set; }

    /// <summary>The byte of a <c>\'hh</c>.</summary>
    public byte Byte { get; private set; }

    /// <summary>The text bytes of a <see cref="RtfTokenKind.Text"/> token.</summary>
    public ReadOnlySpan<byte> Text => data.AsSpan(textStart, textLength);

    /// <summary>The next token.</summary>
    public RtfTokenKind Next()
    {
        while (position < data.Length)
        {
            byte b = data[position];
            switch (b)
            {
                case (byte)'{':
                    position++;
                    return RtfTokenKind.GroupStart;
                case (byte)'}':
                    position++;
                    return RtfTokenKind.GroupEnd;
                case (byte)'\\':
                    return ReadControl();
                case (byte)'\r' or (byte)'\n' or 0:
                    position++;
                    continue;
                default:
                    return ReadText();
            }
        }

        return RtfTokenKind.End;
    }

    /// <summary>Takes the <paramref name="length"/> bytes after <c>\binN</c>, fewer when the file ends first.</summary>
    public ReadOnlySpan<byte> ReadBinary(int length)
    {
        int count = Math.Clamp(length, 0, data.Length - position);
        ReadOnlySpan<byte> bytes = data.AsSpan(position, count);
        position += count;
        return bytes;
    }

    /// <summary>
    /// Skips the rest of the current group, through its closing brace, and returns where that brace was (the file's length
    /// when the group does not close). <c>\bin</c> data is skipped as data.
    /// </summary>
    public int SkipGroup()
    {
        int depth = 1;
        while (position < data.Length)
        {
            switch (data[position])
            {
                case (byte)'{':
                    depth++;
                    position++;
                    break;
                case (byte)'}':
                    position++;
                    if (--depth == 0)
                    {
                        return position - 1;
                    }

                    break;
                case (byte)'\\':
                    if (ReadControl() == RtfTokenKind.ControlWord && Word == "bin" && HasParameter)
                    {
                        _ = ReadBinary(Parameter);
                    }

                    break;
                default:
                    position++;
                    break;
            }
        }

        return data.Length;
    }

    private RtfTokenKind ReadText()
    {
        int start = position;
        while (position < data.Length && data[position] is not ((byte)'{' or (byte)'}' or (byte)'\\' or (byte)'\r' or (byte)'\n' or 0))
        {
            position++;
        }

        textStart = start;
        textLength = position - start;
        return RtfTokenKind.Text;
    }

    private RtfTokenKind ReadControl()
    {
        position++;
        if (position >= data.Length)
        {
            return RtfTokenKind.End;
        }

        byte b = data[position];
        if (!IsLetter(b))
        {
            position++;
            switch (b)
            {
                case (byte)'\'':
                    if (position + 1 < data.Length && Hex(data[position]) is int high && Hex(data[position + 1]) is int low)
                    {
                        position += 2;
                        Byte = (byte)((high << 4) | low);
                        return RtfTokenKind.Hex;
                    }

                    // A broken escape: what follows is read as text.
                    return Next();
                case (byte)'\r' or (byte)'\n':
                    Word = "par";
                    HasParameter = false;
                    Parameter = 0;
                    return RtfTokenKind.ControlWord;
                default:
                    Symbol = (char)b;
                    return RtfTokenKind.ControlSymbol;
            }
        }

        // RTF limits a control word to 32 letters; longer ones are cut and their rest skipped.
        int nameStart = position;
        while (position < data.Length && IsLetter(data[position]))
        {
            position++;
        }

        ReadOnlySpan<byte> name = data.AsSpan(nameStart, Math.Min(position - nameStart, 32));
        Word = Intern(name);
        HasParameter = false;
        Parameter = 0;
        if (position < data.Length && (data[position] == (byte)'-' || IsDigit(data[position])))
        {
            bool negative = data[position] == (byte)'-';
            int digitsStart = negative ? position + 1 : position;
            int at = digitsStart;
            long value = 0;
            while (at < data.Length && IsDigit(data[at]))
            {
                if (value < int.MaxValue)
                {
                    value = (value * 10) + (data[at] - '0');
                }

                at++;
            }

            if (at > digitsStart)
            {
                HasParameter = true;
                Parameter = (int)Math.Clamp(negative ? -value : value, int.MinValue, int.MaxValue);
                position = at;
            }
        }

        // One space ends a control word and belongs to it.
        if (position < data.Length && data[position] == (byte)' ')
        {
            position++;
        }

        return RtfTokenKind.ControlWord;
    }

    private string Intern(ReadOnlySpan<byte> name)
    {
        Span<char> chars = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            chars[i] = (char)name[i];
        }

        if (lookup.TryGetValue(chars, out string? word))
        {
            return word;
        }

        word = new string(chars);
        if (interned.Count < MaxInterned)
        {
            interned[word] = word;
        }

        return word;
    }

    private static bool IsLetter(byte b) => (uint)((b | 0x20) - 'a') <= 'z' - 'a';

    private static bool IsDigit(byte b) => (uint)(b - '0') <= 9;

    private static int? Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => null,
    };
}
