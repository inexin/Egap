using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Egap.Documents.Html;

/// <summary>
/// MHTML (RFC 2557) read into the model: the message's HTML page goes through the HTML reader, and its images, style
/// sheets and fonts come from the message's other parts, by Content-Location or by cid: URL, as Chromium's and Word's web
/// archives link them (Egap 6.6 design Q9).
/// </summary>
internal static class MhtmlReader
{
    public static Document Read(ReadOnlySpan<byte> bytes, HtmlLoadOptions options, out IReadOnlyList<ConversionWarning> warnings)
    {
        MimeEntity root = MimeReader.Read(bytes, options.Limits);
        List<MimeEntity> leaves = [.. root.Leaves()];

        // The page is the part the multipart's start parameter names (RFC 2387 3.2), else the first HTML part.
        MimeEntity? page = null;
        if (root.TypeParameter("start") is { Length: > 0 } start)
        {
            string id = start.Trim().TrimStart('<').TrimEnd('>');
            page = leaves.Find(l => l.ContentId == id);
        }

        page ??= leaves.Find(l => l.MediaType is "text/html" or "application/xhtml+xml")
            ?? throw new DocumentException("The web archive has no HTML page.");

        Uri? contentBase = Absolute(root.Header("Content-Base") ?? root.ContentLocation, null);
        Uri? pageUri = Absolute(page.ContentLocation, contentBase) ?? contentBase;
        Dictionary<string, MimeEntity> byLocation = new(StringComparer.Ordinal);
        Dictionary<string, MimeEntity> byId = new(StringComparer.Ordinal);
        foreach (MimeEntity leaf in leaves)
        {
            if (Absolute(leaf.ContentLocation, contentBase ?? pageUri) is { } location)
            {
                byLocation.TryAdd(Key(location), leaf);
            }

            if (leaf.ContentId is { } id)
            {
                byId.TryAdd(id, leaf);
            }
        }

        Func<Uri, byte[]?>? outside = options.ResourceResolver;
        byte[]? Resolve(Uri uri)
        {
            // Chromium names style sheets it made by cid: URL in their Content-Location; Word links Content-IDs by cid: URL.
            if (uri.Scheme.Equals("cid", StringComparison.OrdinalIgnoreCase))
            {
                string id = Uri.UnescapeDataString(uri.OriginalString[4..]).Trim().TrimStart('<').TrimEnd('>');
                return byId.TryGetValue(id, out MimeEntity? part) || byLocation.TryGetValue(Key(uri), out part) ? part.Body : null;
            }

            if (byLocation.TryGetValue(Key(uri), out MimeEntity? found))
            {
                return found.Body;
            }

            // What the archive does not hold is the caller's to fetch, or nobody's (design Q11).
            return outside?.Invoke(uri);
        }

        HtmlLoadOptions inner = new()
        {
            BaseUri = pageUri ?? options.BaseUri,
            ResourceResolver = Resolve,
            Encoding = options.Encoding,
            Media = options.Media,
            UserStyleSheets = options.UserStyleSheets,
            Limits = options.Limits,
        };

        // A charset on the part decides how its text is read; without one the page's own meta charset does.
        if (page.TypeParameter("charset") is { } charset && MimeEntity.Charset(charset) is not null)
        {
            return HtmlFormat.Parse(page.BodyText(), out warnings, inner);
        }

        return HtmlFormat.Load(page.Body, out warnings, inner);
    }

    /// <summary>A URL as written in a part, made absolute against the base when relative, or null.</summary>
    private static Uri? Absolute(string? url, Uri? baseUri)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = url.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? absolute) && !url.StartsWith('/'))
        {
            return absolute;
        }

        return baseUri is not null && Uri.TryCreate(baseUri, url, out Uri? combined) ? combined : null;
    }

    /// <summary>The form two URLs are matched in: absolute, without a fragment.</summary>
    private static string Key(Uri uri) => !uri.IsAbsoluteUri ? uri.OriginalString
        : uri.GetLeftPart(UriPartial.Query) is { Length: > 0 } left ? left : uri.AbsoluteUri.Split('#')[0];
}

/// <summary>
/// The model written as MHTML in the shape Chromium saves pages in (Egap 6.6 design Q9): a multipart/related message whose
/// first part is the HTML page in quoted-printable, followed by its pictures and fonts in base64, each with a
/// Content-Location the page links to.
/// </summary>
internal static class MhtmlWriter
{
    private const string Root = "file:///page/";

    public static byte[] Write(Document document, HtmlSaveOptions options, out IReadOnlyList<ConversionWarning> warnings)
    {
        List<(string Location, string ContentType, ReadOnlyMemory<byte> Data)> resources = [];
        // The same picture shown twice is read as two images with the same bytes; it is written once.
        Dictionary<string, string> written = new(StringComparer.Ordinal);
        HtmlSaveOptions inner = new()
        {
            EmbedImages = false,
            WritePageSetup = options.WritePageSetup,
            ImageWriter = picture =>
            {
                if (options.ImageWriter?.Invoke(picture) is { } url)
                {
                    return url;
                }

                if (picture.Image.Bytes.IsEmpty)
                {
                    return null;
                }

                string hash = Convert.ToHexString(SHA256.HashData(picture.Image.Bytes.Span));
                if (!written.TryGetValue(hash, out string? name))
                {
                    name = "image" + (resources.Count + 1).ToString(CultureInfo.InvariantCulture) + ImageExtension(picture.Image.ContentType);
                    written[hash] = name;
                    resources.Add((Root + name, picture.Image.ContentType, picture.Image.Bytes));
                }

                return name;
            },
        };

        string fonts = FontFaces(document.EmbeddedFonts, (name, type, data) => resources.Add((Root + name, type, data)));
        string html = HtmlWriter.Write(document, inner, null, fonts, out warnings);
        byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
        string boundary = "----MultipartBoundary--" + Convert.ToHexString(SHA256.HashData(htmlBytes).AsSpan(0, 16)) + "----";

        StringBuilder message = new();
        message.Append("From: <Saved by Egap>\r\n");
        message.Append("Snapshot-Content-Location: ").Append(Root).Append("index.html\r\n");
        if (document.Properties.Title is { Length: > 0 } title)
        {
            message.Append("Subject: ").Append(EncodedWord(title)).Append("\r\n");
        }

        message.Append("MIME-Version: 1.0\r\n");
        message.Append("Content-Type: multipart/related;\r\n\ttype=\"text/html\";\r\n\tboundary=\"").Append(boundary).Append("\"\r\n\r\n");
        message.Append("--").Append(boundary).Append("\r\n");
        message.Append("Content-Type: text/html; charset=\"utf-8\"\r\n");
        message.Append("Content-Transfer-Encoding: quoted-printable\r\n");
        message.Append("Content-Location: ").Append(Root).Append("index.html\r\n\r\n");
        message.Append(QuotedPrintable.Encode(htmlBytes)).Append("\r\n");
        foreach ((string location, string contentType, ReadOnlyMemory<byte> data) in resources)
        {
            message.Append("--").Append(boundary).Append("\r\n");
            message.Append("Content-Type: ").Append(contentType).Append("\r\n");
            message.Append("Content-Transfer-Encoding: base64\r\n");
            message.Append("Content-Location: ").Append(location).Append("\r\n\r\n");
            string base64 = Convert.ToBase64String(data.Span);
            for (int i = 0; i < base64.Length; i += 76)
            {
                message.Append(base64, i, Math.Min(76, base64.Length - i)).Append("\r\n");
            }
        }

        message.Append("--").Append(boundary).Append("--\r\n");
        return Encoding.ASCII.GetBytes(message.ToString());
    }

    /// <summary>
    /// @font-face rules for the document's embedded fonts, each font handed to <paramref name="store"/> under the name the
    /// rule links to.
    /// </summary>
    internal static string FontFaces(IList<EmbeddedFont> fonts, Action<string, string, ReadOnlyMemory<byte>> store)
    {
        StringBuilder css = new();
        for (int i = 0; i < fonts.Count; i++)
        {
            EmbeddedFont font = fonts[i];
            (string extension, string type) = FontType(font.Data.Span);
            string name = "font" + (i + 1).ToString(CultureInfo.InvariantCulture) + extension;
            store(name, type, font.Data);
            css.Append("@font-face { font-family: \"").Append(font.Family.Replace("\"", "", StringComparison.Ordinal)).Append("\"; src: url(\"")
                .Append(name).Append("\"); font-weight: ").Append(font.Bold ? "bold" : "normal").Append("; font-style: ")
                .Append(font.Italic ? "italic" : "normal").Append("; }\n");
        }

        return css.ToString();
    }

    /// <summary>A font's file extension and media type (RFC 8081) from its first bytes.</summary>
    internal static (string Extension, string ContentType) FontType(ReadOnlySpan<byte> data) => data switch
    {
        [(byte)'O', (byte)'T', (byte)'T', (byte)'O', ..] => (".otf", "font/otf"),
        [(byte)'w', (byte)'O', (byte)'F', (byte)'F', ..] => (".woff", "font/woff"),
        [(byte)'w', (byte)'O', (byte)'F', (byte)'2', ..] => (".woff2", "font/woff2"),
        _ => (".ttf", "font/ttf"),
    };

    internal static string ImageExtension(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/svg+xml" => ".svg",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tif",
        _ => ".bin",
    };

    private static string EncodedWord(string text)
    {
        bool ascii = text.All(c => c is >= ' ' and < '\u007f');
        return ascii ? text : "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "?=";
    }
}
