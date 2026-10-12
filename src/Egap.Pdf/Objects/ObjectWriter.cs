using System.Globalization;

using Egap.Pdf.IO;
using Egap.Pdf.Parsing;

namespace Egap.Pdf.Objects;

/// <summary>Writes objects in PDF syntax (ISO 32000-2, 7.3). What it writes, the parser reads back to equal objects.</summary>
internal static class ObjectWriter
{
    /// <summary>The deepest nesting written; deeper trees can only come from code and would risk the stack.</summary>
    public const int MaxDepth = 4096;

    private static readonly byte[] HexDigits = "0123456789ABCDEF"u8.ToArray();

    public static byte[] ToBytes(PdfObject value, bool includeStreamData = true)
    {
        ByteWriter output = new();
        Write(value, output, includeStreamData);
        return output.ToArray();
    }

    public static void Write(PdfObject value, ByteWriter output, bool includeStreamData = true)
    {
        Write(value, output, includeStreamData, 0, null);
    }

    /// <summary>
    /// Writes an object with every reference passed through <paramref name="remap"/>: the reference to write instead, or
    /// <see langword="null"/> to write <c>null</c> (for a reference to an object that is not written).
    /// </summary>
    public static void Write(PdfObject value, ByteWriter output, Func<PdfReference, PdfReference?> remap)
    {
        Write(value, output, includeStreamData: true, 0, remap);
    }

    private static void Write(PdfObject value, ByteWriter output, bool includeStreamData, int depth, Func<PdfReference, PdfReference?>? remap)
    {
        // The depth limit alone does not keep the stack safe: how deep it can go depends on the thread and the runtime.
        if (depth > MaxDepth || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new InvalidOperationException($"Objects nest too deep to write (more than {Math.Min(depth, MaxDepth)} levels).");
        }

        switch (value)
        {
            case PdfNull:
                output.Write("null"u8);
                break;
            case PdfBoolean boolean:
                output.Write(boolean.Value ? "true"u8 : "false"u8);
                break;
            case PdfInteger integer:
                output.WriteInteger(integer.Value);
                break;
            case PdfReal real:
                WriteReal(real, output);
                break;
            case PdfName name:
                WriteName(name.Bytes, output);
                break;
            case PdfString text:
                WriteString(text, output);
                break;
            case PdfReference original:
                if ((remap is null ? original : remap(original)) is not PdfReference reference)
                {
                    output.Write("null"u8);
                    break;
                }

                output.WriteInteger(reference.ObjectNumber);
                output.Write((byte)' ');
                output.WriteInteger(reference.Generation);
                output.Write(" R"u8);
                break;
            case PdfArray array:
                output.Write((byte)'[');
                for (int i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        output.Write((byte)' ');
                    }

                    Write(array[i], output, includeStreamData, depth + 1, remap);
                }

                output.Write((byte)']');
                break;
            case PdfDictionary dictionary:
                WriteDictionary(dictionary, output, null, includeStreamData, depth, remap);
                break;
            case PdfStream stream when includeStreamData:
                ReadOnlySpan<byte> data = stream.Data is BytesData bytes ? bytes.Span : stream.GetEncodedBytes();
                WriteDictionary(stream.Dictionary, output, data.Length, includeStreamData, depth, remap);
                output.Write("\nstream\n"u8);
                output.Write(data);
                output.Write("\nendstream"u8);
                break;
            case PdfStream stream:
                WriteDictionary(stream.Dictionary, output, stream.EncodedLength, includeStreamData, depth, remap);
                output.WriteAscii(string.Create(CultureInfo.InvariantCulture, $" stream ({stream.EncodedLength} bytes)"));
                break;
            default:
                throw new ArgumentException($"Unknown object type {value.GetType()}.", nameof(value));
        }
    }

    /// <summary>Writes a dictionary; for a stream's dictionary, <paramref name="length"/> replaces <c>/Length</c>.</summary>
    private static void WriteDictionary(PdfDictionary dictionary, ByteWriter output, long? length, bool includeStreamData, int depth, Func<PdfReference, PdfReference?>? remap)
    {
        output.Write("<<"u8);
        bool wroteLength = false;
        foreach ((PdfName key, PdfObject item) in dictionary.Entries)
        {
            output.Write((byte)' ');
            WriteName(key.Bytes, output);
            output.Write((byte)' ');
            if (length is long count && key.Bytes.SequenceEqual("Length"u8))
            {
                output.WriteInteger(count);
                wroteLength = true;
            }
            else
            {
                Write(item, output, includeStreamData, depth + 1, remap);
            }
        }

        if (length is long missing && !wroteLength)
        {
            output.Write(" /Length "u8);
            output.WriteInteger(missing);
        }

        output.Write(" >>"u8);
    }

    private static void WriteName(ReadOnlySpan<byte> name, ByteWriter output)
    {
        output.Write((byte)'/');
        foreach (byte b in name)
        {
            // Bytes outside the printable range, '#' and delimiters are written as #xx (7.3.5).
            if (b is < 0x21 or > 0x7E or (byte)'#' || CharClass.IsDelimiter(b))
            {
                output.Write((byte)'#');
                output.Write(HexDigits[b >> 4]);
                output.Write(HexDigits[b & 0xF]);
            }
            else
            {
                output.Write(b);
            }
        }
    }

    private static void WriteString(PdfString text, ByteWriter output)
    {
        if (text.IsHex)
        {
            output.Write((byte)'<');
            foreach (byte b in text.Bytes)
            {
                output.Write(HexDigits[b >> 4]);
                output.Write(HexDigits[b & 0xF]);
            }

            output.Write((byte)'>');
            return;
        }

        output.Write((byte)'(');
        foreach (byte b in text.Bytes)
        {
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    output.Write((byte)'\\');
                    output.Write(b);
                    break;
                case (byte)'\r':
                    // A raw carriage return would read back as a line feed (7.3.4.2).
                    output.Write("\\r"u8);
                    break;
                default:
                    output.Write(b);
                    break;
            }
        }

        output.Write((byte)')');
    }

    private static void WriteReal(PdfReal real, ByteWriter output)
    {
        if (real.Text is byte[] text)
        {
            output.Write(text);
            return;
        }

        output.WriteAscii(FormatReal(real.Value));
    }

    /// <summary>
    /// The shortest text that reads back to the same double, in plain notation (PDF has no exponents), always with a point so
    /// it reads back as a real and not an integer.
    /// </summary>
    public static string FormatReal(double value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        int e = text.IndexOf('E', StringComparison.Ordinal);
        if (e >= 0)
        {
            text = ExpandExponent(text[..e], int.Parse(text.AsSpan(e + 1), CultureInfo.InvariantCulture));
        }

        return text.Contains('.', StringComparison.Ordinal) ? text : text + ".0";
    }

    private static string ExpandExponent(string mantissa, int exponent)
    {
        bool negative = mantissa.StartsWith('-');
        string digits = mantissa.TrimStart('-');
        int point = digits.IndexOf('.', StringComparison.Ordinal);
        if (point < 0)
        {
            point = digits.Length;
        }

        digits = digits.Replace(".", "", StringComparison.Ordinal);
        point += exponent;
        string plain;
        if (point <= 0)
        {
            plain = "0." + new string('0', -point) + digits;
        }
        else if (point >= digits.Length)
        {
            plain = digits + new string('0', point - digits.Length);
        }
        else
        {
            plain = digits[..point] + "." + digits[point..];
        }

        return negative ? "-" + plain : plain;
    }
}
