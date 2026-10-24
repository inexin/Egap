using System.Xml.Linq;

namespace Egap.Documents.Html;

/// <summary>A manifest item (EPUB 3.3 5.6): its id, full name in the container, media type and properties.</summary>
internal sealed record EpubItem(string Id, string Name, string MediaType, IReadOnlyList<string> Properties, string? MediaOverlay)
{
    public bool Has(string property) => Properties.Contains(property, StringComparer.Ordinal);

    public bool IsXhtml => MediaType is "application/xhtml+xml" or "text/html" or "application/x-dtbook+xml";
}

/// <summary>A spine item: the manifest item, whether it is in the reading order, and its own rendition properties.</summary>
internal sealed record EpubSpineItem(EpubItem Item, bool Linear, IReadOnlyList<string> Properties);

/// <summary>One entry of the table of contents (EPUB 3 nav or EPUB 2 NCX): its label, target and depth from 0.</summary>
internal sealed record EpubNavEntry(string Title, string? Target, string? Fragment, int Level);

/// <summary>
/// The package document (EPUB 3.3 5, OPF 2.0.1) the container points at: metadata, manifest, spine, the navigation and the
/// rendition properties, read from EPUB 2.0.1 and EPUB 3.0 to 3.3 books alike (Egap 6.6 design Q2).
/// </summary>
internal sealed class EpubPackage
{
    public static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    public static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    public static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    public static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    public static readonly XNamespace Ops = "http://www.idpf.org/2007/ops";
    public static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";

    public required string Name { get; init; }

    public required string Version { get; init; }

    public required XElement Metadata { get; init; }

    public string UniqueIdentifier { get; init; } = "";

    public List<string> Identifiers { get; } = [];

    public Dictionary<string, EpubItem> Manifest { get; } = new(StringComparer.Ordinal);

    public List<EpubSpineItem> Spine { get; } = [];

    public string? Toc { get; init; }

    public string? Direction { get; init; }

    public bool FixedLayout { get; init; }

    public string? CoverImage { get; set; }

    /// <summary>Reads META-INF/container.xml and the package document it names first.</summary>
    /// <exception cref="DocumentException">The container or package document is missing or broken.</exception>
    public static EpubPackage Read(EpubContainer container)
    {
        XDocument containerXml = container.GetXml("META-INF/container.xml")
            ?? throw new DocumentException("The book has no META-INF/container.xml, so it is not an EPUB.");
        string? path = containerXml.Descendants(Container + "rootfile")
            .Where(r => (string?)r.Attribute("media-type") is null or "application/oebps-package+xml")
            .Select(r => (string?)r.Attribute("full-path")).FirstOrDefault(p => p is not null);
        string name = (path is null ? null : EpubContainer.Resolve("", path, out _))
            ?? throw new DocumentException("The book's container.xml names no package document.");
        XElement root = container.GetXml(name)?.Root ?? throw new DocumentException($"The book's package document {name} is missing.");
        XNamespace ns = root.Name.Namespace;
        XElement metadata = root.Element(ns + "metadata") ?? new XElement(ns + "metadata");

        // EPUB 2 books sometimes wrap Dublin Core in dc-metadata.
        if (metadata.Element(ns + "dc-metadata") is { } wrapped)
        {
            metadata = new XElement(metadata.Name, metadata.Attributes(), wrapped.Elements(), metadata.Elements().Where(e => e != wrapped));
        }

        string version = (string?)root.Attribute("version") ?? "2.0";
        string uniqueId = (string?)root.Attribute("unique-identifier") ?? "";
        XElement? spine = root.Element(ns + "spine");
        string? layout = metadata.Elements(ns + "meta").FirstOrDefault(m => (string?)m.Attribute("property") == "rendition:layout")?.Value.Trim();
        EpubPackage package = new()
        {
            Name = name,
            Version = version,
            Metadata = metadata,
            UniqueIdentifier = metadata.Elements(Dc + "identifier").FirstOrDefault(i => (string?)i.Attribute("id") == uniqueId)?.Value.Trim()
                ?? metadata.Elements(Dc + "identifier").FirstOrDefault()?.Value.Trim() ?? "",
            Toc = (string?)spine?.Attribute("toc"),
            Direction = (string?)spine?.Attribute("page-progression-direction"),
            FixedLayout = layout == "pre-paginated",
        };

        package.Identifiers.AddRange(metadata.Elements(Dc + "identifier").Select(i => i.Value.Trim()));
        foreach (XElement item in root.Element(ns + "manifest")?.Elements(ns + "item") ?? [])
        {
            if ((string?)item.Attribute("id") is not { } id || (string?)item.Attribute("href") is not { } href
                || EpubContainer.Resolve(name, href, out _) is not { } full)
            {
                continue;
            }

            package.Manifest.TryAdd(id, new EpubItem(id, full, ((string?)item.Attribute("media-type") ?? "").Trim().ToLowerInvariant(),
                Split((string?)item.Attribute("properties")), (string?)item.Attribute("media-overlay")));
        }

        foreach (XElement itemref in spine?.Elements(ns + "itemref") ?? [])
        {
            if ((string?)itemref.Attribute("idref") is { } idref && package.Manifest.TryGetValue(idref, out EpubItem? item))
            {
                package.Spine.Add(new EpubSpineItem(item, (string?)itemref.Attribute("linear") != "no", Split((string?)itemref.Attribute("properties"))));
            }
        }

        // The cover image: EPUB 3's cover-image property, else EPUB 2's <meta name="cover">.
        package.CoverImage = package.Manifest.Values.FirstOrDefault(i => i.Has("cover-image"))?.Name;
        if (package.CoverImage is null && metadata.Elements(ns + "meta").FirstOrDefault(m => (string?)m.Attribute("name") == "cover") is { } cover
            && (string?)cover.Attribute("content") is { } coverId)
        {
            package.CoverImage = package.Manifest.GetValueOrDefault(coverId)?.Name
                ?? package.Manifest.Values.FirstOrDefault(i => i.Name.EndsWith("/" + coverId, StringComparison.Ordinal) && i.MediaType.StartsWith("image/", StringComparison.Ordinal))?.Name;
        }

        return package;
    }

    /// <summary>The Dublin Core values of an element (dc:title, dc:creator…), in order, trimmed and non-empty.</summary>
    public IEnumerable<string> Values(string element) =>
        Metadata.Elements(Dc + element).Select(e => e.Value.Trim()).Where(v => v.Length > 0);

    /// <summary>An EPUB 3 meta property's value, or EPUB 2's named meta's content, or null.</summary>
    public string? Meta(string property)
    {
        XNamespace ns = Metadata.Name.Namespace;
        return Metadata.Elements(ns + "meta").FirstOrDefault(m => (string?)m.Attribute("property") == property && m.Attribute("refines") is null)?.Value.Trim()
            ?? (string?)Metadata.Elements(ns + "meta").FirstOrDefault(m => (string?)m.Attribute("name") == property)?.Attribute("content");
    }

    /// <summary>The book's table of contents: the EPUB 3 nav document's toc nav, else the EPUB 2 NCX's navMap.</summary>
    public List<EpubNavEntry> ReadNavigation(EpubContainer container, out List<EpubNavEntry> landmarks)
    {
        landmarks = [];
        if (Manifest.Values.FirstOrDefault(i => i.Has("nav")) is { } nav && container.Get(nav.Name) is { } navBytes)
        {
            XDocument? xml = XhtmlText.TryLoad(navBytes, container.Limits);
            if (xml?.Root is { } root)
            {
                List<EpubNavEntry> toc = [];
                foreach (XElement element in root.Descendants().Where(e => e.Name.LocalName == "nav"))
                {
                    string type = (string?)element.Attribute(Ops + "type") ?? (string?)element.Attribute("type") ?? "";
                    List<string> types = Split(type);
                    if (types.Contains("toc") && toc.Count == 0)
                    {
                        NavList(element, nav.Name, 0, toc);
                    }
                    else if (types.Contains("landmarks"))
                    {
                        NavList(element, nav.Name, 0, landmarks);
                    }
                }

                if (toc.Count > 0)
                {
                    return toc;
                }
            }
        }

        string? ncxName = Toc is not null && Manifest.TryGetValue(Toc, out EpubItem? tocItem) ? tocItem.Name
            : Manifest.Values.FirstOrDefault(i => i.MediaType == "application/x-dtbncx+xml")?.Name;
        List<EpubNavEntry> entries = [];
        if (ncxName is not null && container.GetXml(ncxName)?.Root is { } ncx)
        {
            XNamespace ns = ncx.Name.Namespace;
            if (ncx.Element(ns + "navMap") is { } map)
            {
                NavPoints(map, ns, ncxName, 0, entries, container.Limits.MaxDepth);
            }
        }

        return entries;
    }

    private static void NavList(XElement nav, string from, int level, List<EpubNavEntry> entries)
    {
        if (level > 64 || nav.Elements().FirstOrDefault(e => e.Name.LocalName == "ol") is not { } list)
        {
            return;
        }

        foreach (XElement li in list.Elements().Where(e => e.Name.LocalName == "li"))
        {
            XElement? label = li.Elements().FirstOrDefault(e => e.Name.LocalName is "a" or "span");
            if (label is not null)
            {
                string title = Collapse(label.Value);
                string? target = null, fragment = null;
                if (label.Name.LocalName == "a" && (string?)label.Attribute("href") is { } href)
                {
                    target = EpubContainer.Resolve(from, href, out fragment);
                }

                if (title.Length > 0)
                {
                    entries.Add(new EpubNavEntry(title, target, fragment, level));
                }
            }

            NavList(li, from, level + 1, entries);
        }
    }

    private static void NavPoints(XElement parent, XNamespace ns, string from, int level, List<EpubNavEntry> entries, int maxDepth)
    {
        if (level > Math.Min(maxDepth, 64))
        {
            return;
        }

        foreach (XElement point in parent.Elements(ns + "navPoint"))
        {
            string title = Collapse(point.Element(ns + "navLabel")?.Element(ns + "text")?.Value ?? "");
            string? fragment = null;
            string? target = (string?)point.Element(ns + "content")?.Attribute("src") is { } src ? EpubContainer.Resolve(from, src, out fragment) : null;
            if (title.Length > 0)
            {
                entries.Add(new EpubNavEntry(title, target, fragment, level));
            }

            NavPoints(point, ns, from, level + 1, entries, maxDepth);
        }
    }

    internal static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static List<string> Split(string? value) => value is null ? [] : [.. value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)];
}
