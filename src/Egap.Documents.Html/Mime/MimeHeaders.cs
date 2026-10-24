using System.Text;

namespace Egap.Documents.Html;

/// <summary>Header field values: Content-Type and its parameters (RFC 2045 5.1) and encoded words (RFC 2047).</summary>
internal static class MimeHeaders
{
    /// <summary>The type/subtype of a Content-Type value in lower case; text/plain when there is none.</summary>
    public static string MediaType(string? contentType)
    {
        if (contentType is null)
        {
            return "text/plain";
        }

        int semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        string type = (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim().ToLowerInvariant();
        return type.Contains('/', StringComparison.Ordinal) ? type : "text/plain";
    }

    /// <summary>A parameter of a structured value such as Content-Type, its quotes and escapes removed, or null.</summary>
    public static string? Parameter(string? value, string name)
    {
        if (value is null)
        {
            return null;
        }

        int i = value.IndexOf(';', StringComparison.Ordinal);
        while (i >= 0 && i < value.Length)
        {
            i++;
            while (i < value.Length && value[i] is ' ' or '\t' or '\r' or '\n')
            {
                i++;
            }

            int equals = value.IndexOf('=', i);
            if (equals < 0)
            {
                return null;
            }

            string key = value[i..equals].Trim();
            i = equals + 1;
            while (i < value.Length && value[i] is ' ' or '\t')
            {
                i++;
            }

            string parameter;
            if (i < value.Length && value[i] == '"')
            {
                StringBuilder quoted = new();
                i++;
                while (i < value.Length && value[i] != '"')
                {
                    if (value[i] == '\\' && i + 1 < value.Length)
                    {
                        i++;
                    }

                    quoted.Append(value[i]);
                    i++;
                }

                parameter = quoted.ToString();
                i = value.IndexOf(';', Math.Min(i + 1, value.Length));
            }
            else
            {
                int end = value.IndexOf(';', i);
                parameter = (end >= 0 ? value[i..end] : value[i..]).Trim();
                i = end;
            }

            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return parameter;
            }
        }

        return null;
    }

    /// <summary>A header value with its RFC 2047 encoded words (=?charset?B?…?= and =?charset?Q?…?=) decoded.</summary>
    public static string DecodeWords(string value)
    {
        if (!value.Contains("=?", StringComparison.Ordinal))
        {
            return value;
        }

        StringBuilder text = new(value.Length);
        int i = 0;
        bool lastWasWord = false;
        while (i < value.Length)
        {
            int start = value.IndexOf("=?", i, StringComparison.Ordinal);
            if (start < 0 || TryWord(value, start, out string? decoded, out int end) is false)
            {
                int next = start < 0 ? value.Length : start + 2;
                text.Append(value, i, next - i);
                lastWasWord = false;
                i = next;
                continue;
            }

            // White space between two encoded words is not shown (RFC 2047 6.2).
            string between = value[i..start];
            if (!(lastWasWord && string.IsNullOrWhiteSpace(between)))
            {
                text.Append(between);
            }

            text.Append(decoded);
            lastWasWord = true;
            i = end;
        }

        return text.ToString();
    }

    private static bool TryWord(string value, int start, out string? decoded, out int end)
    {
        decoded = null;
        end = start;
        int charsetEnd = value.IndexOf('?', start + 2);
        if (charsetEnd < 0 || charsetEnd + 2 >= value.Length || value[charsetEnd + 2] != '?')
        {
            return false;
        }

        int textEnd = value.IndexOf("?=", charsetEnd + 3, StringComparison.Ordinal);
        if (textEnd < 0)
        {
            return false;
        }

        // A language suffix (RFC 2231 5) is dropped.
        string charset = value[(start + 2)..charsetEnd].Split('*')[0];
        char encoding = char.ToUpperInvariant(value[charsetEnd + 1]);
        string encoded = value[(charsetEnd + 3)..textEnd];
        if (MimeEntity.Charset(charset) is not { } textEncoding)
        {
            return false;
        }

        byte[] bytes;
        if (encoding == 'B')
        {
            try
            {
                bytes = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                return false;
            }
        }
        else if (encoding == 'Q')
        {
            bytes = QuotedPrintable.Decode(Encoding.ASCII.GetBytes(encoded.Replace('_', ' ')));
        }
        else
        {
            return false;
        }

        decoded = textEncoding.GetString(bytes);
        end = textEnd + 2;
        return true;
    }
}
