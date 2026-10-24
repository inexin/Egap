using System.Text;
using System.Text.Unicode;

namespace Egap.Documents.Html;

/// <summary>
/// Reads a MIME message (RFC 2045, RFC 2046) as MHTML holds it (RFC 2557): header fields, multipart bodies split at their
/// boundaries, and base64 and quoted-printable bodies decoded, within <see cref="DocumentLimits"/> (entities, part and total
/// sizes, nesting).
/// </summary>
internal sealed class MimeReader
{
    private readonly DocumentLimits limits;
    private int entities;
    private long total;

    private MimeReader(DocumentLimits limits)
    {
        this.limits = limits;
    }

    /// <exception cref="DocumentException">The bytes are not a MIME message.</exception>
    /// <exception cref="DocumentLimitException">Past a limit.</exception>
    public static MimeEntity Read(ReadOnlySpan<byte> message, DocumentLimits limits)
    {
        if (message.Length > limits.MaxTotalSize)
        {
            throw new DocumentLimitException($"The message is {message.Length} bytes, more than the limit of {limits.MaxTotalSize}.");
        }

        MimeEntity root = new MimeReader(limits).Entity(message, 0);
        if (root.Headers.Count == 0)
        {
            throw new DocumentException("The file is not a MIME message: it has no header fields.");
        }

        return root;
    }

    private MimeEntity Entity(ReadOnlySpan<byte> bytes, int depth)
    {
        if (++entities > limits.MaxEntries)
        {
            throw new DocumentLimitException($"The message has more than the limit of {limits.MaxEntries} parts.");
        }

        if (depth > limits.MaxDepth)
        {
            throw new DocumentLimitException($"The message's parts nest deeper than the limit of {limits.MaxDepth}.");
        }

        MimeEntity entity = new();
        int bodyStart = ReadHeaders(bytes, entity.Headers);
        ReadOnlySpan<byte> body = bytes[bodyStart..];
        if (entity.IsMultipart && entity.TypeParameter("boundary") is { Length: > 0 } boundary)
        {
            foreach (Range part in SplitParts(body, Encoding.ASCII.GetBytes("--" + boundary)))
            {
                entity.Parts.Add(Entity(body[part], depth + 1));
            }

            return entity;
        }

        string encoding = entity.Header("Content-Transfer-Encoding")?.Trim().ToLowerInvariant() ?? "7bit";
        byte[] decoded = encoding switch
        {
            "base64" => Base64(body),
            "quoted-printable" => QuotedPrintable.Decode(body),
            _ => body.ToArray(),
        };

        if (decoded.Length > limits.MaxPartSize)
        {
            throw new DocumentLimitException($"A part is {decoded.Length} bytes, more than the limit of {limits.MaxPartSize}.");
        }

        total += decoded.Length;
        if (total > limits.MaxTotalSize)
        {
            throw new DocumentLimitException($"The message's parts add up to more than the limit of {limits.MaxTotalSize} bytes.");
        }

        entity.Body = decoded;
        return entity;
    }

    /// <summary>Reads header fields up to the empty line, unfolding them; returns where the body starts.</summary>
    private static int ReadHeaders(ReadOnlySpan<byte> bytes, List<KeyValuePair<string, string>> headers)
    {
        int i = 0;
        string? name = null;
        StringBuilder value = new();
        while (i < bytes.Length)
        {
            int lineEnd = bytes[i..].IndexOf((byte)'\n');
            int next = lineEnd < 0 ? bytes.Length : i + lineEnd + 1;
            ReadOnlySpan<byte> line = bytes[i..(lineEnd < 0 ? bytes.Length : i + lineEnd)].TrimEnd((byte)'\r');
            if (line.IsEmpty)
            {
                Flush();
                return next;
            }

            if (line[0] is (byte)' ' or (byte)'\t')
            {
                if (name is not null)
                {
                    value.Append(' ').Append(Text(line).Trim());
                }
            }
            else
            {
                int colon = line.IndexOf((byte)':');
                if (colon <= 0)
                {
                    // Not a header line: the headers ended without an empty line, and this is the body.
                    Flush();
                    return i;
                }

                Flush();
                name = Text(line[..colon]).Trim();
                value.Append(Text(line[(colon + 1)..]).Trim());
            }

            i = next;
        }

        Flush();
        return bytes.Length;

        void Flush()
        {
            if (name is not null)
            {
                headers.Add(new(name, MimeHeaders.DecodeWords(value.ToString())));
            }

            name = null;
            value.Clear();
        }
    }

    /// <summary>Header bytes as text: UTF-8 when valid (RFC 6532), else Latin-1.</summary>
    private static string Text(ReadOnlySpan<byte> bytes) => Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);

    /// <summary>The body parts between the delimiter lines (RFC 2046 5.1.1); the preamble and epilogue are dropped.</summary>
    private static List<Range> SplitParts(ReadOnlySpan<byte> body, byte[] delimiter)
    {
        List<Range> parts = [];
        int partStart = -1;
        int i = 0;
        while (i < body.Length)
        {
            int lineEnd = body[i..].IndexOf((byte)'\n');
            int next = lineEnd < 0 ? body.Length : i + lineEnd + 1;
            ReadOnlySpan<byte> line = body[i..(lineEnd < 0 ? body.Length : i + lineEnd)];
            if (line.StartsWith(delimiter))
            {
                ReadOnlySpan<byte> rest = line[delimiter.Length..];
                bool close = rest.StartsWith("--"u8);
                if (close)
                {
                    rest = rest[2..];
                }

                if (rest.Trim(" \t\r"u8).IsEmpty)
                {
                    if (partStart >= 0)
                    {
                        // The line break before the delimiter belongs to it.
                        int end = i;
                        if (end > partStart && body[end - 1] == (byte)'\n')
                        {
                            end--;
                            if (end > partStart && body[end - 1] == (byte)'\r')
                            {
                                end--;
                            }
                        }

                        parts.Add(new Range(partStart, end));
                    }

                    if (close)
                    {
                        return parts;
                    }

                    partStart = next;
                }
            }

            i = next;
        }

        // A message cut short: the last part runs to the end.
        if (partStart >= 0 && partStart < body.Length)
        {
            parts.Add(new Range(partStart, body.Length));
        }

        return parts;
    }

    /// <summary>
    /// Base64 decoded leniently, as mail readers do: characters outside the alphabet skipped, padding optional, and the
    /// spare bits of a last group (which Word sets) ignored.
    /// </summary>
    private static byte[] Base64(ReadOnlySpan<byte> body)
    {
        byte[] output = new byte[body.Length / 4 * 3 + 3];
        int length = 0, bits = 0, count = 0;
        foreach (byte b in body)
        {
            int value = b switch
            {
                >= (byte)'A' and <= (byte)'Z' => b - 'A',
                >= (byte)'a' and <= (byte)'z' => b - 'a' + 26,
                >= (byte)'0' and <= (byte)'9' => b - '0' + 52,
                (byte)'+' => 62,
                (byte)'/' => 63,
                _ => -1,
            };

            if (value < 0)
            {
                continue;
            }

            bits = (bits << 6) | value;
            count += 6;
            if (count >= 8)
            {
                count -= 8;
                output[length++] = (byte)(bits >> count);
                bits &= (1 << count) - 1;
            }
        }

        return output.AsSpan(0, length).ToArray();
    }
}
