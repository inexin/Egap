using System.Text;

using Egap.Pdf.Objects;

namespace Egap.Pdf;

/// <summary>
/// A PDF string: a sequence of bytes, written either as a literal <c>(...)</c> or as hex <c>&lt;...&gt;</c>. A string read
/// from a file keeps its bytes and its form, so an unchanged string writes back the same way.
/// </summary>
public sealed class PdfString : PdfObject, IEquatable<PdfString>
{
    private readonly byte[] bytes;

    private PdfString(byte[] bytes, bool isHex)
    {
        this.bytes = bytes;
        IsHex = isHex;
    }

    /// <summary>The string's bytes, escapes resolved.</summary>
    public ReadOnlySpan<byte> Bytes => bytes;

    /// <summary>Whether the string is written in hex form.</summary>
    public bool IsHex { get; }

    /// <summary>A string holding the given bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <param name="hex">Whether to write it in hex form, which suits binary data such as file IDs.</param>
    public static PdfString FromBytes(ReadOnlySpan<byte> bytes, bool hex = false)
    {
        return new PdfString(bytes.ToArray(), hex);
    }

    /// <summary>
    /// A text string (7.9.2.2): PDFDocEncoding when every character fits, otherwise UTF-16BE with a byte order mark.
    /// </summary>
    public static PdfString FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[]? single = PdfDocEncoding.TryEncode(text);
        if (single is not null && !StartsWithByteOrderMark(single))
        {
            return new PdfString(single, isHex: false);
        }

        byte[] utf16 = new byte[2 + (text.Length * 2)];
        utf16[0] = 0xFE;
        utf16[1] = 0xFF;
        Encoding.BigEndianUnicode.GetBytes(text, utf16.AsSpan(2));
        return new PdfString(utf16, isHex: false);
    }

    internal static PdfString FromParsed(byte[] bytes, bool hex)
    {
        return new PdfString(bytes, hex);
    }

    /// <summary>
    /// The string decoded as a text string (7.9.2.2): UTF-16BE or UTF-8 when it starts with their byte order mark,
    /// otherwise PDFDocEncoding. Language escapes (an ESC, a language code and an ESC) in Unicode text are removed.
    /// </summary>
    public string ToText()
    {
        ReadOnlySpan<byte> span = bytes;
        if (span is [0xFE, 0xFF, ..])
        {
            return RemoveLanguageEscapes(Encoding.BigEndianUnicode.GetString(span[2..(2 + ((span.Length - 2) & ~1))]));
        }

        if (span is [0xFF, 0xFE, ..])
        {
            // Little-endian UTF-16 is not allowed, but some producers write it.
            return RemoveLanguageEscapes(Encoding.Unicode.GetString(span[2..(2 + ((span.Length - 2) & ~1))]));
        }

        if (span is [0xEF, 0xBB, 0xBF, ..])
        {
            return RemoveLanguageEscapes(Encoding.UTF8.GetString(span[3..]));
        }

        return PdfDocEncoding.Decode(span);
    }

    private static bool StartsWithByteOrderMark(byte[] bytes)
    {
        return bytes is [0xFE, 0xFF, ..] or [0xFF, 0xFE, ..] or [0xEF, 0xBB, 0xBF, ..];
    }

    private static string RemoveLanguageEscapes(string text)
    {
        int escape = text.IndexOf('\u001B', StringComparison.Ordinal);
        if (escape < 0)
        {
            return text;
        }

        StringBuilder result = new(text.Length);
        int start = 0;
        while (escape >= 0)
        {
            int end = text.IndexOf('\u001B', escape + 1);
            if (end < 0)
            {
                break;
            }

            result.Append(text, start, escape - start);
            start = end + 1;
            escape = text.IndexOf('\u001B', start);
        }

        return result.Append(text, start, text.Length - start).ToString();
    }

    /// <summary>Whether two strings have the same bytes; the form they are written in does not matter.</summary>
    public bool Equals(PdfString? other)
    {
        return other is not null && other.bytes.AsSpan().SequenceEqual(bytes);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfString);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.AddBytes(bytes);
        return hash.ToHashCode();
    }
}
