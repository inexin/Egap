namespace Egap.Pdf.Objects;

/// <summary>PDFDocEncoding (ISO 32000-2, Annex D.2), the single-byte encoding of text strings without a byte order mark.</summary>
internal static class PdfDocEncoding
{
    /// <summary>The character for each byte; '�' marks bytes the encoding leaves undefined.</summary>
    private static readonly char[] ToChar = Build();

    private static readonly Dictionary<char, byte> FromChar = BuildReverse();

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        return string.Create(bytes.Length, bytes.ToArray(), static (chars, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                chars[i] = ToChar[source[i]];
            }
        });
    }

    /// <summary>Encodes text, or returns <see langword="null"/> when a character has no PDFDocEncoding byte.</summary>
    public static byte[]? TryEncode(string text)
    {
        byte[] bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            if (!FromChar.TryGetValue(text[i], out bytes[i]))
            {
                return null;
            }
        }

        return bytes;
    }

    private static char[] Build()
    {
        char[] map = new char[256];
        for (int i = 0; i < 256; i++)
        {
            map[i] = (char)i;
        }

        // Control characters other than tab, line feed and carriage return are undefined.
        for (int i = 0; i < 0x18; i++)
        {
            if (i is not (0x09 or 0x0A or 0x0D))
            {
                map[i] = '�';
            }
        }

        "˘ˇˆ˙˝˛˚˜".CopyTo(map.AsSpan(0x18));
        map[0x7F] = '�';
        ("•†‡…—–ƒ⁄‹›−‰„“”‘"
            + "’‚™ﬁﬂŁŒŠŸŽıłœšž�€").CopyTo(map.AsSpan(0x80));
        map[0xAD] = '�';
        return map;
    }

    private static Dictionary<char, byte> BuildReverse()
    {
        Dictionary<char, byte> reverse = [];
        for (int i = 0; i < 256; i++)
        {
            if (ToChar[i] != '�')
            {
                reverse[ToChar[i]] = (byte)i;
            }
        }

        return reverse;
    }
}
