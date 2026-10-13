using System.Globalization;

namespace Egap.Pdf.Pages;

/// <summary>
/// Adds drawing to existing pages (Phase 3 design Q5) and resources to their resource dictionaries (Q6). The page's own
/// streams are never rewritten: drawn over, its content is bracketed by a two-byte <c>q</c> stream in front and a <c>Q</c>
/// before the new drawing at the end, so the new drawing starts from a clean graphics state whatever the old content left
/// behind; drawn under, the new drawing comes first inside its own <c>q</c> and <c>Q</c>.
/// </summary>
internal static class PageDrawing
{
    /// <summary>The prefix of names Egap gives new resources (Q6).</summary>
    public const string NamePrefix = "Eg";

    /// <summary>Adds a drawing over or under the page's content.</summary>
    public static void Add(PdfPage page, ReadOnlySpan<byte> drawing, PdfCanvasPlacement placement)
    {
        PdfDocument document = page.Document;
        page = document.Pages.EnsureTree(page);
        List<PdfObject> old = [.. PdfContent.ContentStreams(page, null).Select(s => s.Entry)];
        PdfArray contents = [];
        if (placement == PdfCanvasPlacement.Under)
        {
            contents.Add(Stream(document, [.. "q\n"u8, .. drawing, .. "\nQ\n"u8]));
            foreach (PdfObject entry in old)
            {
                contents.Add(entry);
            }
        }
        else
        {
            if (old.Count > 0)
            {
                contents.Add(Stream(document, "q\n"u8));
                foreach (PdfObject entry in old)
                {
                    contents.Add(entry);
                }
            }

            contents.Add(Stream(document, old.Count > 0 ? [.. "\nQ\n"u8, .. drawing] : drawing));
        }

        page.Dictionary["Contents"] = contents.Count == 1 ? contents[0] : contents;
    }

    /// <summary>
    /// The page's own resource dictionary, to add to: a shallow copy made on the page first when its resources are inherited,
    /// indirect (so perhaps shared with other pages) or missing (Q6). The copy holds the same font and image objects.
    /// </summary>
    public static PdfDictionary OwnResources(PdfPage page)
    {
        if (page.Dictionary["Resources"] is PdfDictionary own)
        {
            return own;
        }

        PdfDictionary copy = page.Resources?.Clone() ?? [];
        page.Dictionary["Resources"] = copy;
        return copy;
    }

    /// <summary>
    /// Adds a resource to the page and returns its name. A reference the category already holds keeps the name it has;
    /// otherwise the name is <c>Eg</c> and a counter, past every name in the category.
    /// </summary>
    public static PdfName AddResource(PdfPage page, PdfName category, PdfObject value)
    {
        PdfDictionary resources = OwnResources(page);
        PdfDictionary entries;
        switch (resources[category])
        {
            case PdfDictionary direct:
                entries = direct;
                break;
            case PdfReference reference when reference.Resolve() is PdfDictionary shared:
                entries = shared.Clone();
                resources[category] = entries;
                break;
            default:
                entries = [];
                resources[category] = entries;
                break;
        }

        if (value is PdfReference wanted)
        {
            foreach ((PdfName key, PdfObject existing) in entries.Entries)
            {
                if (existing is PdfReference held && held.Equals(wanted))
                {
                    return key;
                }
            }
        }

        PdfName name = FreeName(entries, category);
        entries[name] = value;
        return name;
    }

    /// <summary>
    /// The prefix of a new resource's name: <c>Eg</c> and a letter for its category (<c>EgX</c> for XObjects, <c>EgG</c> for
    /// graphics states and so on), so no two categories share a name, which pdf.js's resource caches mix up.
    /// </summary>
    public static string Prefix(PdfName category)
    {
        return NamePrefix + category.Value switch
        {
            "XObject" => "X",
            "ExtGState" => "G",
            "Shading" => "S",
            "Pattern" => "P",
            "Font" => "F",
            "ColorSpace" => "C",
            "Properties" => "M",
            _ => "",
        };
    }

    /// <summary>The first name of the category's prefix and a counter that the entries do not hold.</summary>
    public static PdfName FreeName(PdfDictionary entries, PdfName category)
    {
        string prefix = Prefix(category);
        for (int i = 0; ; i++)
        {
            PdfName name = PdfName.Get(string.Create(CultureInfo.InvariantCulture, $"{prefix}{i}"));
            if (entries[name] is null)
            {
                return name;
            }
        }
    }

    private static PdfReference Stream(PdfDocument document, ReadOnlySpan<byte> data)
    {
        PdfStream stream = new();
        stream.SetDecodedBytes(data, compress: data.Length > 64);
        return document.Objects.Add(stream);
    }
}
