using System.Text;

namespace Egap.Documents.Html;

/// <summary>
/// One MIME entity (RFC 2045, RFC 2046) as MHTML uses it: its header fields, and either its decoded body or, for a multipart
/// entity, its parts.
/// </summary>
internal sealed class MimeEntity
{
    public List<KeyValuePair<string, string>> Headers { get; } = [];

    /// <summary>The body with its transfer encoding undone; empty for a multipart entity.</summary>
    public byte[] Body { get; set; } = [];

    public List<MimeEntity> Parts { get; } = [];

    /// <summary>The first value of a header field (case-insensitive name), unfolded, or null.</summary>
    public string? Header(string name)
    {
        foreach (KeyValuePair<string, string> header in Headers)
        {
            if (header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        return null;
    }

    /// <summary>The media type in lower case (text/plain when none is given, as RFC 2045 5.2 says).</summary>
    public string MediaType => MimeHeaders.MediaType(Header("Content-Type"));

    /// <summary>A parameter of the Content-Type field, or null.</summary>
    public string? TypeParameter(string name) => MimeHeaders.Parameter(Header("Content-Type"), name);

    public bool IsMultipart => MediaType.StartsWith("multipart/", StringComparison.Ordinal);

    /// <summary>The Content-ID without its angle brackets, or null.</summary>
    public string? ContentId => Header("Content-ID")?.Trim().TrimStart('<').TrimEnd('>') is { Length: > 0 } id ? id : null;

    /// <summary>The Content-Location, with RFC 2047 words decoded, or null.</summary>
    public string? ContentLocation => Header("Content-Location")?.Trim() is { Length: > 0 } location ? location : null;

    /// <summary>The leaf entities, depth first.</summary>
    public IEnumerable<MimeEntity> Leaves()
    {
        Stack<MimeEntity> work = new();
        work.Push(this);
        while (work.Count > 0)
        {
            MimeEntity entity = work.Pop();
            if (entity.IsMultipart)
            {
                for (int i = entity.Parts.Count - 1; i >= 0; i--)
                {
                    work.Push(entity.Parts[i]);
                }
            }
            else
            {
                yield return entity;
            }
        }
    }

    /// <summary>The body as text, in the charset its Content-Type names (UTF-8 when none or an unknown one).</summary>
    public string BodyText()
    {
        Encoding encoding = TypeParameter("charset") is { } charset && Charset(charset) is { } named ? named : Encoding.UTF8;
        return encoding.GetString(Body);
    }

    /// <summary>
    /// The encoding a MIME charset names: .NET's own (UTF-8, UTF-16, ISO-8859-1, US-ASCII), else a code page such as
    /// windows-1252, without registering a provider for the whole process; null for one neither knows.
    /// </summary>
    public static Encoding? Charset(string name)
    {
        name = name.Trim().Trim('"');
        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(name);
        }
    }
}
