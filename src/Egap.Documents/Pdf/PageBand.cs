using System.Text;
using System.Text.RegularExpressions;

using Egap.Pdf;


namespace Egap.Documents.Pdf;

/// <summary>
/// A header or footer that <see cref="PdfConvert"/> puts on every page (<see cref="PdfConvertOptions.Header"/> and
/// <see cref="PdfConvertOptions.Footer"/>), in the top or
/// bottom margin: plain text in up to three places (<see cref="Text"/>), or HTML styled with the page's own CSS
/// (<see cref="Html"/>). Both take the <see cref="PdfPlaceholders"/>: <c>{page}</c>, <c>{total-pages}</c>,
/// <c>{label}</c>, <c>{date}</c>, <c>{time}</c>, <c>{title}</c> and <c>{file}</c>.
/// </summary>
/// <example>
/// <code>
/// PdfConvert.FromHtml(html, "invoice.pdf", new PdfConvertOptions
/// {
///     Header = PageBand.Html("&lt;div class='hdr'&gt;ACME &lt;span&gt;{date}&lt;/span&gt;&lt;/div&gt;"),
///     Footer = PageBand.Text(center: "Page {page} of {total-pages}"),
/// });
/// </code>
/// </example>
public sealed partial record PageBand
{
    private PageBand()
    {
    }

    /// <summary>Gets the text at the left; null for none.</summary>
    public string? Left { get; private init; }

    /// <summary>Gets the text in the middle; null for none.</summary>
    public string? Center { get; private init; }

    /// <summary>Gets the text at the right; null for none.</summary>
    public string? Right { get; private init; }

    /// <summary>Gets the HTML, for a band made with <see cref="Html"/>; null for a text band.</summary>
    public string? HtmlContent { get; private init; }

    /// <summary>Gets the font size of a text band, in points. Defaults to 9.</summary>
    public double FontSize { get; init; } = 9;

    /// <summary>Gets the font family of a text band; null (the default) for the document's own.</summary>
    public string? FontFamily { get; init; }

    /// <summary>A band of plain text at the left, in the middle and at the right, each one optional.</summary>
    public static PageBand Text(string? left = null, string? center = null, string? right = null)
    {
        return new PageBand { Left = left, Center = center, Right = right };
    }

    /// <summary>
    /// A band of HTML across the margin. When the document was HTML, the band is styled with the document's own style
    /// sheets; images and links are read as the document's are. Applying it needs the Egap.Documents.Html package and
    /// <c>HtmlFormat.Register()</c>; without them the conversion throws a <see cref="NotSupportedException"/> that names them.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="html"/> is null.</exception>
    public static PageBand Html(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return new PageBand { HtmlContent = html };
    }

    /// <summary>
    /// Puts the band in the margin boxes along the top (a header) or the bottom (a footer) of every section, in place of
    /// the document's own headers or footers and the boxes there.
    /// </summary>
    internal void Apply(Document document, bool header, PageBandContext context)
    {
        MarginBoxPosition[] positions = header
            ? [MarginBoxPosition.TopLeft, MarginBoxPosition.TopCenter, MarginBoxPosition.TopRight]
            : [MarginBoxPosition.BottomLeft, MarginBoxPosition.BottomCenter, MarginBoxPosition.BottomRight];
        foreach (Section section in document.Sections)
        {
            foreach (HeaderFooter story in section.HeadersFooters.Where(s => s.IsHeader == header).ToList())
            {
                story.Remove();
            }

            foreach (MarginBox box in section.MarginBoxes.Where(b => positions.Contains(b.Position)).ToList())
            {
                box.Remove();
            }

            if (HtmlContent is not null)
            {
                MarginBox box = section.MarginBoxes.GetOrAdd(positions[1]);
                foreach (Block block in context.HtmlBlocks(HtmlContent))
                {
                    box.Blocks.Add(document.Import(block));
                }

                continue;
            }

            foreach ((string? text, MarginBoxPosition position) in (ReadOnlySpan<(string?, MarginBoxPosition)>)[(Left, positions[0]), (Center, positions[1]), (Right, positions[2])])
            {
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                DocumentBuilder builder = new(document);
                builder.MoveToMarginBox(position);
                builder.Font.Size = FontSize;
                if (FontFamily is not null)
                {
                    builder.Font.FontAscii = builder.Font.FontHighAnsi = FontFamily;
                }

                Write(builder, text, context);
            }
        }
    }

    /// <summary>Writes a template: text with its fixed placeholders filled, and page fields for the per-page ones.</summary>
    internal static void Write(DocumentBuilder builder, string template, PageBandContext context)
    {
        int at = 0;
        foreach (Match match in Placeholder().Matches(template))
        {
            builder.Write(template[at..match.Index]);
            at = match.Index + match.Length;
            if (PageBandContext.FieldCode(match) is { } code)
            {
                builder.InsertField(code, "1");
            }
            else
            {
                builder.Write(context.FillFixed(match.Value));
            }
        }

        builder.Write(template[at..]);
    }

    [GeneratedRegex(@"\{\{|\{(page|total-pages|pages|label|date|time|title|file)(?::([^}]*))?\}")]
    internal static partial Regex Placeholder();
}

/// <summary>What a band is filled from: the document's title and file, the time of the conversion, and its style sheets.</summary>
internal sealed class PageBandContext(Document document, string? file, string? folder, string? styleSheets, Func<string, string?, Document>? readHtml = null)
{
    private readonly PdfPlaceholderValues fixedValues = new()
    {
        Now = DateTimeOffset.Now,
        Title = document.Properties.Title,
        File = file is null ? null : Path.GetFileName(file),
    };

    /// <summary>The field code for a per-page placeholder ({page} and {total-pages}, with a number format); null for the others.</summary>
    public static string? FieldCode(Match match)
    {
        string format = match.Groups[2].Success ? match.Groups[2].Value : "";
        string picture = format.Length > 0 ? " \\# \"" + format.Replace("\"", "", StringComparison.Ordinal) + "\"" : "";
        return match.Groups[1].Value switch
        {
            "page" or "label" => "PAGE" + picture,
            "pages" or "total-pages" => "NUMPAGES" + picture,
            _ => null,
        };
    }

    /// <summary>A placeholder that is the same on every page, filled.</summary>
    public string FillFixed(string placeholder) => PdfPlaceholders.Fill(placeholder, fixedValues);

    /// <summary>A band's HTML read into blocks, styled with the document's style sheets, its placeholders filled.</summary>
    public IReadOnlyList<Block> HtmlBlocks(string html)
    {
        // The per-page placeholders become fields: they are marked with private-use characters through the HTML reader.
        StringBuilder marked = new();
        int at = 0;
        foreach (Match match in PageBand.Placeholder().Matches(html))
        {
            marked.Append(html, at, match.Index - at);
            at = match.Index + match.Length;
            marked.Append(FieldCode(match) is { } code ? "" + code + "" : FillFixed(match.Value));
        }

        marked.Append(html, at, html.Length - at);
        // HTML comes from its add-on: without it registered, this names the package to add.
        Document band = (readHtml ?? DocumentFormatRegistry.TextReaderFor(DocumentFormatKind.Html))("<!DOCTYPE html><html><head>" + styleSheets + "</head><body style='margin:0'>" + marked + "</body></html>", folder);
        foreach (Run run in band.Descendants<Run>().Where(r => r.Text.Contains('', StringComparison.Ordinal)).ToList())
        {
            Split(band, run);
        }

        return [.. band.Sections[0].Body.Blocks];
    }

    /// <summary>Turns the marked field codes in a run into fields, the text around them kept in runs of the same format.</summary>
    private static void Split(Document band, Run run)
    {
        DocumentBuilder builder = new(band);
        builder.MoveTo(run);
        builder.Font.Apply(run.Format);
        string text = run.Text;
        int at = 0;
        while (text.IndexOf('', at) is int start and >= 0 && text.IndexOf('', start) is int end and >= 0)
        {
            builder.Write(text[at..start]);
            builder.InsertField(text[(start + 1)..end], "1");
            at = end + 1;
        }

        builder.Write(text[at..]);
        run.Remove();
    }
}
