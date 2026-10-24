using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Xhtml;

namespace Egap.Documents.Html;

/// <summary>
/// The model written as an EPUB 3.3 book (Egap 6.6 design Q2, Q4, Q5, Q8): XHTML chapters from the HTML writer, split at
/// section breaks and level 1 headings (a book read from EPUB keeps its chapter files), a navigation document to
/// <see cref="EpubSaveOptions.NavigationDepth"/>, a toc.ncx and guide for EPUB 2 readers, the pictures, the embedded fonts
/// when asked (subset and obfuscated), and accessibility metadata from what the document has.
/// </summary>
internal sealed class EpubWriter
{
    private const string Content = "OEBPS/";

    // EPUB 3.3 core media types for images (3.2); others are written as PNG.
    private static readonly HashSet<string> CoreImages = new(StringComparer.Ordinal) { "image/gif", "image/jpeg", "image/png", "image/svg+xml", "image/webp" };

    private readonly Document document;
    private readonly EpubSaveOptions options;
    private readonly WarningCollector warnings = new();
    private readonly List<Resource> resources = [];
    private readonly Dictionary<string, Resource> images = new(StringComparer.Ordinal);
    private readonly HashSet<string> names = new(StringComparer.OrdinalIgnoreCase) { "nav.xhtml", "toc.ncx", "content.opf", "cover.xhtml" };
    private readonly Dictionary<string, string> bookmarkFiles = new(StringComparer.Ordinal);
    private string language = "und";
    private string identifier = "";
    private bool pictures;
    private bool picturesWithoutText;
    private bool coverPage;

    private EpubWriter(Document document, EpubSaveOptions options)
    {
        this.document = document;
        this.options = options;
    }

    /// <summary>A file of the book other than a chapter: its name under OEBPS/, media type, bytes and manifest properties.</summary>
    private sealed record Resource(string Id, string Name, string MediaType, byte[] Data, string? Properties = null);

    /// <summary>A chapter file: its name under OEBPS/, manifest id, blocks and the outline entries in it.</summary>
    private sealed class Chapter(string name, string id)
    {
        public string Name { get; } = name;

        public string Id { get; } = id;

        public List<Block> Blocks { get; } = [];

        public string Xhtml { get; set; } = "";

        public bool Cover { get; init; }
    }

    private sealed record NavEntry(string Title, string Href, int Level);

    public static IReadOnlyList<ConversionWarning> Write(Document source, Stream stream, EpubSaveOptions options)
    {
        if (options.NavigationDepth is < 1 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.NavigationDepth, "NavigationDepth must be 1 to 9.");
        }

        // A copy, so headings can get the ids the navigation links to without changing the caller's document.
        Document document = source.Clone();
        EpubWriter writer = new(document, options);
        writer.WriteBook(stream);
        return writer.warnings.ToList();
    }

    private void WriteBook(Stream stream)
    {
        language = document.Styles.DefaultRunFormat.Language is { Length: > 0 } lang ? lang : "und";
        identifier = options.Identifier is { Length: > 0 } given ? given
            : document.Extensions.Get<EpubBookExtension>(EpubBookExtension.Format)?.UniqueIdentifier is { Length: > 0 } kept ? kept : MadeIdentifier();

        List<NavEntry> navigation = [];
        List<Chapter> chapters = Split();
        AddHeadingIds(chapters, navigation);
        if (options.EmbedFonts && document.EmbeddedFonts.Count > 0)
        {
            resources.Add(new Resource("fonts-css", "css/fonts.css", "text/css", Encoding.UTF8.GetBytes(EmbedFonts())));
        }

        Resource? cover = options.CoverImage is { } coverImage ? Image(coverImage, "cover-image") : null;
        coverPage = cover is not null;

        // A book read from EPUB has its cover as a first section; its picture is the cover image.
        if (cover is null && chapters.FirstOrDefault(c => c.Cover)?.Blocks.SelectMany(b => b.Descendants<Picture>()).FirstOrDefault() is { } coverPicture)
        {
            cover = Image(coverPicture.Image, "cover-image");
        }
        foreach (Chapter chapter in chapters)
        {
            chapter.Xhtml = WriteChapter(chapter);
        }

        if (navigation.Count == 0)
        {
            // The navigation document needs an entry (EPUB 3.3 7.4.2): each chapter by its first words.
            int n = 1;
            foreach (Chapter chapter in chapters.Any(c => !c.Cover) ? chapters.Where(c => !c.Cover) : chapters)
            {
                string first = chapter.Blocks.OfType<Paragraph>().Select(p => EpubPackage.Collapse(Clean(p.GetText()))).FirstOrDefault(t => t.Length > 0) ?? "";
                string label = first.Length == 0 ? "Chapter " + n.ToString(CultureInfo.InvariantCulture) : first.Length > 60 ? first[..57] + "..." : first;
                navigation.Add(new NavEntry(label, chapter.Name, 0));
                n++;
            }
        }

        string title = document.Properties.Title is { Length: > 0 } t ? Clean(t) : navigation.FirstOrDefault()?.Title ?? "Untitled";
        using ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true);

        // The mimetype entry comes first and stored, so its bytes sit at offset 38 (EPUB 3.3 OCF 4.3).
        Entry(zip, "mimetype", Encoding.ASCII.GetBytes("application/epub+zip"), CompressionLevel.NoCompression);
        Entry(zip, "META-INF/container.xml", Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">\n" +
            "  <rootfiles>\n    <rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/>\n  </rootfiles>\n</container>\n"));
        if (resources.Any(r => r.Properties == "obfuscated"))
        {
            Entry(zip, "META-INF/encryption.xml", Encoding.UTF8.GetBytes(EncryptionXml()));
        }

        if (coverPage && cover is not null)
        {
            Entry(zip, Content + "cover.xhtml", Encoding.UTF8.GetBytes(CoverPage(cover)));
        }

        foreach (Chapter chapter in chapters)
        {
            Entry(zip, Content + chapter.Name, Encoding.UTF8.GetBytes(chapter.Xhtml));
        }

        Entry(zip, Content + "nav.xhtml", Encoding.UTF8.GetBytes(NavDocument(navigation, chapters, coverPage)));
        Entry(zip, Content + "toc.ncx", Encoding.UTF8.GetBytes(Ncx(navigation, title)));
        foreach (Resource resource in resources)
        {
            Entry(zip, Content + resource.Name, resource.Data);
        }

        Entry(zip, Content + "content.opf", Encoding.UTF8.GetBytes(PackageDocument(chapters, cover, navigation, title)));
    }

    private static void Entry(ZipArchive zip, string name, byte[] data, CompressionLevel level = CompressionLevel.Optimal)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, level);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using Stream output = entry.Open();
        output.Write(data);
    }

    /// <summary>A urn:uuid from the title and author, so the same book saved twice keeps its identifier.</summary>
    private string MadeIdentifier()
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("egap-epub\u0000" + document.Properties.Title + "\u0000" + document.Properties.Author));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return "urn:uuid:" + new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString();
    }

    // ---- Chapters ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The chapter files: the sections a book read from EPUB came in, else the document split at section breaks and level 1
    /// headings as <see cref="EpubSaveOptions.SplitAt"/> says (design Q4).
    /// </summary>
    private List<Chapter> Split()
    {
        List<Chapter> chapters = [];
        bool fromEpub = document.Sections.All(s => s.Extensions.Get<EpubChapterExtension>(EpubChapterExtension.Format) is not null);
        Chapter? current = null;
        foreach (Section section in document.Sections)
        {
            EpubChapterExtension? kept = section.Extensions.Get<EpubChapterExtension>(EpubChapterExtension.Format);
            bool breakHere = current is null || fromEpub || (options.SplitAt & EpubSplit.SectionBreak) != 0 || kept is not null;
            if (breakHere)
            {
                current = NewChapter(chapters, kept);
            }

            foreach (Block block in section.Body.Blocks)
            {
                if (!fromEpub && (options.SplitAt & EpubSplit.HeadingLevel1) != 0 && current!.Blocks.Count > 0
                    && block is Paragraph paragraph && paragraph.GetEffectiveFormat().OutlineLevel == 0 && current.Blocks.Any(HasText))
                {
                    current = NewChapter(chapters, null);
                }

                current!.Blocks.Add(block);
            }
        }

        return chapters;

        static bool HasText(Block block) => block is not Paragraph p || p.GetText().Trim().Length > 0 || p.Inlines.Any(i => i is Picture);
    }

    private Chapter NewChapter(List<Chapter> chapters, EpubChapterExtension? kept)
    {
        string name = "chapter" + (chapters.Count + 1).ToString("D3", CultureInfo.InvariantCulture) + ".xhtml";
        string id = "c" + (chapters.Count + 1).ToString("D3", CultureInfo.InvariantCulture);
        if (kept is not null && Path.GetFileName(kept.Name) is { Length: > 0 } file && SafeName(file) && !names.Contains(file))
        {
            name = Path.GetExtension(file) is ".xhtml" or ".html" or ".htm" or ".xml" ? Path.ChangeExtension(file, ".xhtml") : file + ".xhtml";
            id = IsNcName(kept.Id) && !chapters.Any(c => c.Id == kept.Id) ? kept.Id : id;
        }

        while (!names.Add(name))
        {
            name = "c" + name;
        }

        Chapter chapter = new(name, id) { Cover = kept?.Properties.Contains("cover") == true };
        chapters.Add(chapter);
        return chapter;
    }

    private static bool SafeName(string name) => name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && !name.StartsWith('.');

    private static bool IsNcName(string id) => id.Length > 0 && (char.IsAsciiLetter(id[0]) || id[0] == '_') && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>
    /// Gives each outline paragraph down to the navigation depth a bookmark when it has none, notes which file every bookmark
    /// lands in, and collects the navigation entries.
    /// </summary>
    private void AddHeadingIds(List<Chapter> chapters, List<NavEntry> navigation)
    {
        int n = 1;
        HashSet<string> used = [.. document.GetBookmarks().Select(b => b.Name)];
        foreach (Chapter chapter in chapters)
        {
            foreach (Block block in chapter.Blocks)
            {
                foreach (BookmarkStart start in block.Descendants<BookmarkStart>())
                {
                    bookmarkFiles.TryAdd(start.Name, chapter.Name);
                }

                foreach (Paragraph paragraph in block is Paragraph p ? [p] : block.Descendants<Paragraph>())
                {
                    int level = paragraph.GetEffectiveFormat().OutlineLevel;
                    if (level >= options.NavigationDepth || chapter.Cover)
                    {
                        continue;
                    }

                    string text = EpubPackage.Collapse(Clean(paragraph.GetText()));
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    string? id = paragraph.Inlines.OfType<BookmarkStart>().FirstOrDefault()?.Name;
                    if (id is null)
                    {
                        do
                        {
                            id = "toc-" + n.ToString(CultureInfo.InvariantCulture);
                            n++;
                        }
                        while (!used.Add(id));

                        paragraph.Inlines.Insert(0, new BookmarkStart(document, id));
                        bookmarkFiles[id] = chapter.Name;
                    }

                    navigation.Add(new NavEntry(text, chapter.Name + "#" + Uri.EscapeDataString(id), level));
                }
            }
        }
    }

    private string WriteChapter(Chapter chapter)
    {
        HtmlSaveOptions html = new()
        {
            EmbedImages = false,
            WritePageSetup = false,
            ImageWriter = picture =>
            {
                if (picture.Image.Bytes.IsEmpty)
                {
                    return null;
                }

                pictures = true;
                picturesWithoutText |= string.IsNullOrWhiteSpace(picture.Description);
                return Image(picture.Image, null)?.Name;
            },
        };

        string page = HtmlWriter.Write(document, html, chapter.Blocks, null, out IReadOnlyList<ConversionWarning> pageWarnings);
        foreach (ConversionWarning warning in pageWarnings)
        {
            warnings.Add(warning.Kind, warning.Message);
        }

        return Xhtml(page, chapter);
    }

    /// <summary>
    /// The HTML writer's page as XHTML for EPUB: parsed and serialized as XML, with the XHTML and EPUB namespaces, the
    /// language, links to other chapters pointed at their files, and links to files the book does not hold dropped.
    /// </summary>
    private string Xhtml(string html, Chapter chapter)
    {
        HtmlParser parser = new(new HtmlParserOptions { IsScripting = false });
        IDocument dom = parser.ParseDocument(html);
        IElement root = dom.DocumentElement;
        root.SetAttribute("xmlns", "http://www.w3.org/1999/xhtml");
        root.SetAttribute("xmlns:epub", "http://www.idpf.org/2007/ops");
        root.SetAttribute("lang", language);
        root.SetAttribute("xml:lang", language);
        if (dom.Head is { } head && head.QuerySelector("title") is null)
        {
            IElement title = dom.CreateElement("title");
            title.TextContent = document.Properties.Title is { Length: > 0 } t ? t : chapter.Name;
            head.AppendChild(title);
        }

        if (dom.Head is { } fontHead && resources.Any(r => r.Id == "fonts-css"))
        {
            IElement link = dom.CreateElement("link");
            link.SetAttribute("rel", "stylesheet");
            link.SetAttribute("href", "css/fonts.css");
            fontHead.AppendChild(link);
        }

        foreach (IElement link in dom.QuerySelectorAll("a[href]"))
        {
            string href = link.GetAttribute("href")!;
            if (href.StartsWith('#'))
            {
                string id = Uri.UnescapeDataString(href[1..]);
                if (bookmarkFiles.TryGetValue(id, out string? file) && file != chapter.Name)
                {
                    link.SetAttribute("href", file + href);
                }
                else if (file is null)
                {
                    link.RemoveAttribute("href");
                }
            }
            else if (!Uri.TryCreate(href, UriKind.Absolute, out _))
            {
                // A relative link into the book it came from, which this book does not hold.
                link.RemoveAttribute("href");
            }
        }

        // A caption the HTML reader kept from a figure the model has no box for is a paragraph outside a figure.
        foreach (IElement caption in dom.QuerySelectorAll("figcaption").Where(e => e.ParentElement?.LocalName != "figure").ToList())
        {
            IElement paragraph = dom.CreateElement("p");
            foreach (IAttr attribute in caption.Attributes.ToList())
            {
                paragraph.SetAttribute(attribute.Name, attribute.Value);
            }

            while (caption.FirstChild is { } child)
            {
                paragraph.AppendChild(child);
            }

            caption.Replace(paragraph);
        }

        // XHTML has no name attribute on <a>; ids carry the targets.
        foreach (IElement anchor in dom.QuerySelectorAll("a[name]"))
        {
            if (anchor.GetAttribute("id") is null)
            {
                anchor.SetAttribute("id", anchor.GetAttribute("name"));
            }

            anchor.RemoveAttribute("name");
        }

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE html>\n" + root.ToHtml(XhtmlMarkupFormatter.Instance) + "\n";
    }

    /// <summary>The book's file for an image, written once per image: core types as they are, others as PNG.</summary>
    private Resource? Image(ImageData image, string? properties)
    {
        // The same picture shown twice is read as two images with the same bytes; it is written once.
        string hash = Convert.ToHexString(SHA256.HashData(image.Bytes.Span));
        if (images.TryGetValue(hash, out Resource? existing))
        {
            if (properties is not null && existing.Properties is null)
            {
                Resource marked = existing with { Properties = properties };
                resources[resources.IndexOf(existing)] = marked;
                images[hash] = marked;
                return marked;
            }

            return existing;
        }

        string type = image.ContentType.ToLowerInvariant();
        byte[] data = image.Bytes.ToArray();
        if (type == "image/svg+xml")
        {
            if (SelfContainedSvg(data) is not { } svg)
            {
                warnings.Add(ConversionWarningKind.PictureDropped, "SVG pictures that are not well-formed XML");
                return null;
            }

            data = svg;
        }
        if (!CoreImages.Contains(type))
        {
            try
            {
                using Egami.Raster raster = Egami.Raster.Load(image.Bytes);
                using MemoryStream png = new();
                raster.Save(png, "png");
                data = png.ToArray();
                type = "image/png";
            }
            catch (Exception e) when (e is NotSupportedException or InvalidDataException or FormatException or ArgumentException or Egami.EgamiException)
            {
                warnings.Add(ConversionWarningKind.PictureDropped, "pictures in a format EPUB does not hold and Egami could not convert");
                return null;
            }
        }

        string name = "images/image" + (images.Count + 1).ToString(CultureInfo.InvariantCulture) + MhtmlWriter.ImageExtension(type);
        Resource resource = new("img" + (images.Count + 1).ToString(CultureInfo.InvariantCulture), name, type, data, properties);
        images[hash] = resource;
        resources.Add(resource);
        return resource;
    }

    /// <summary>
    /// An SVG picture with only what a book file may hold on its own: no scripts or event handlers (which would need the
    /// scripted property), no style sheet processing instructions, and no links to files outside it, which the book does
    /// not carry; null when it is not XML.
    /// </summary>
    private byte[]? SelfContainedSvg(byte[] data)
    {
        XDocument svg;
        try
        {
            svg = EpubContainer.LoadXml(data, new DocumentLimits());
        }
        catch (DocumentException)
        {
            return null;
        }

        bool changed = false;
        foreach (XProcessingInstruction instruction in svg.Nodes().OfType<XProcessingInstruction>().Where(p => p.Target == "xml-stylesheet").ToList())
        {
            instruction.Remove();
            changed = true;
        }

        foreach (XElement element in svg.Descendants().ToList())
        {
            if (element.Name.LocalName is "script" || (element.Name.LocalName is "image" or "use" or "feImage" && External(element)))
            {
                element.Remove();
                changed = true;
                continue;
            }

            foreach (XAttribute attribute in element.Attributes().Where(a => a.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) && a.Name.Namespace == XNamespace.None).ToList())
            {
                attribute.Remove();
                changed = true;
            }

            if (element.Name.LocalName == "a" && External(element))
            {
                element.Attributes().Where(a => a.Name.LocalName == "href").Remove();
                changed = true;
            }

            if (element.Name.LocalName == "style" && element.Value.Contains("@import", StringComparison.OrdinalIgnoreCase))
            {
                element.Remove();
                changed = true;
            }
        }

        if (changed)
        {
            warnings.Add(ConversionWarningKind.FormattingDropped, "scripts and links to other files in SVG pictures");
        }

        using MemoryStream output = new();
        svg.Save(output, SaveOptions.DisableFormatting);
        return output.ToArray();

        static bool External(XElement element) => element.Attributes().FirstOrDefault(a => a.Name.LocalName == "href")?.Value.Trim() is { Length: > 0 } href
            && !href.StartsWith('#') && !href.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    // ---- Fonts ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The embedded fonts as book files, subset to the characters the document uses and obfuscated with the IDPF algorithm
    /// (EPUB 3.3 OCF 4.6), with their @font-face rules (design Q5).
    /// </summary>
    private string EmbedFonts()
    {
        HashSet<int> characters = [' '];
        foreach (Paragraph paragraph in document.Descendants<Paragraph>())
        {
            foreach (Rune rune in paragraph.GetText().EnumerateRunes())
            {
                characters.Add(rune.Value);
            }
        }

        StringBuilder css = new();
        List<EmbeddedFont> fonts = [.. document.EmbeddedFonts];
        for (int i = 0; i < fonts.Count; i++)
        {
            EmbeddedFont font = fonts[i];
            byte[] data = Subset(font.Data, characters);
            (string extension, string type) = MhtmlWriter.FontType(data);
            string name = "fonts/font" + (i + 1).ToString(CultureInfo.InvariantCulture) + extension;
            byte[] obfuscated = EpubContainer.Deobfuscate(data, "idpf:" + identifier);
            resources.Add(new Resource("font" + (i + 1).ToString(CultureInfo.InvariantCulture), name, type, obfuscated, "obfuscated"));
            css.Append("@font-face { font-family: \"").Append(font.Family.Replace("\"", "", StringComparison.Ordinal)).Append("\"; src: url(\"../")
                .Append(name).Append("\"); font-weight: ").Append(font.Bold ? "bold" : "normal").Append("; font-style: ")
                .Append(font.Italic ? "italic" : "normal").Append("; }\n");
        }

        return css.ToString();
    }

    private byte[] Subset(ReadOnlyMemory<byte> data, HashSet<int> characters)
    {
        try
        {
            Egami.Fonts.Typeface face = Egami.Fonts.Typeface.FromMemory(data);
            if ((face.Embedding & Egami.Fonts.FontEmbedding.NoSubsetting) != 0)
            {
                return data.ToArray();
            }

            List<ushort> glyphs = [0, .. characters.Select(c => face.GetGlyph(c)).Where(g => g != 0).Distinct()];
            Egami.Fonts.FontSubset subset = Egami.Fonts.FontSubsetter.Subset(face, glyphs, new Egami.Fonts.SubsetOptions
            {
                RetainGlyphIds = true,
                KeepCmap = true,
                LayoutClosure = true,
            });
            return subset.Data.ToArray();
        }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException or FormatException or ArgumentException or IndexOutOfRangeException)
        {
            warnings.Add(ConversionWarningKind.FormattingDropped, "fonts that could not be subset, embedded whole");
            return data.ToArray();
        }
    }

    private string EncryptionXml()
    {
        StringBuilder xml = new("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<encryption xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" xmlns:enc=\"http://www.w3.org/2001/04/xmlenc#\">\n");
        foreach (Resource font in resources.Where(r => r.Properties == "obfuscated"))
        {
            xml.Append("  <enc:EncryptedData>\n    <enc:EncryptionMethod Algorithm=\"").Append(EpubContainer.IdpfObfuscation).Append("\"/>\n")
                .Append("    <enc:CipherData><enc:CipherReference URI=\"").Append(Content).Append(font.Name).Append("\"/></enc:CipherData>\n  </enc:EncryptedData>\n");
        }

        return xml.Append("</encryption>\n").ToString();
    }

    // ---- Navigation and package -----------------------------------------------------------------------------------------

    private string CoverPage(Resource cover)
    {
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE html>\n" +
            $"<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" lang=\"{Attribute(language)}\" xml:lang=\"{Attribute(language)}\">\n" +
            "<head><meta charset=\"utf-8\"/><title>Cover</title><style>body { margin: 0; text-align: center; } img { max-width: 100%; max-height: 100vh; }</style></head>\n" +
            $"<body epub:type=\"cover\"><img src=\"{Attribute(cover.Name)}\" alt=\"Cover\"/></body>\n</html>\n";
    }

    private string NavDocument(List<NavEntry> navigation, List<Chapter> chapters, bool cover)
    {
        StringBuilder nav = new("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE html>\n");
        nav.Append(CultureInfo.InvariantCulture, $"<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" lang=\"{Attribute(language)}\" xml:lang=\"{Attribute(language)}\">\n");
        nav.Append("<head><meta charset=\"utf-8\"/><title>Contents</title></head>\n<body>\n<nav epub:type=\"toc\" id=\"toc\" role=\"doc-toc\">\n<h1>Contents</h1>\n");
        int depth = -1;
        foreach (NavEntry entry in navigation)
        {
            int level = Math.Min(entry.Level, depth + 1);
            if (level > depth)
            {
                nav.Append("<ol>\n");
            }
            else
            {
                nav.Append("</li>\n");
                for (int d = depth; d > level; d--)
                {
                    nav.Append("</ol>\n</li>\n");
                }
            }

            depth = level;
            nav.Append("<li><a href=\"").Append(Attribute(entry.Href)).Append("\">").Append(Text(entry.Title)).Append("</a>");
        }

        for (int d = depth; d >= 0; d--)
        {
            nav.Append("</li>\n</ol>\n");
        }

        nav.Append("</nav>\n<nav epub:type=\"landmarks\" id=\"landmarks\" hidden=\"hidden\">\n<h2>Landmarks</h2>\n<ol>\n");
        if (cover)
        {
            nav.Append("<li><a epub:type=\"cover\" href=\"cover.xhtml\">Cover</a></li>\n");
        }

        nav.Append("<li><a epub:type=\"toc\" href=\"nav.xhtml#toc\">Contents</a></li>\n");
        if (chapters.FirstOrDefault(c => !c.Cover) is { } first)
        {
            nav.Append("<li><a epub:type=\"bodymatter\" href=\"").Append(Attribute(first.Name)).Append("\">Start</a></li>\n");
        }

        nav.Append("</ol>\n</nav>\n</body>\n</html>\n");
        return nav.ToString();
    }

    private string Ncx(List<NavEntry> navigation, string title)
    {
        XNamespace ns = EpubPackage.Ncx;
        XElement map = new(ns + "navMap");
        Stack<(int Level, XElement Element)> open = new();
        int order = 1;
        foreach (NavEntry entry in navigation)
        {
            while (open.Count > 0 && open.Peek().Level >= entry.Level)
            {
                open.Pop();
            }

            XElement point = new(ns + "navPoint",
                new XAttribute("id", "np" + order.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("playOrder", order.ToString(CultureInfo.InvariantCulture)),
                new XElement(ns + "navLabel", new XElement(ns + "text", entry.Title)),
                new XElement(ns + "content", new XAttribute("src", entry.Href)));
            (open.Count > 0 ? open.Peek().Element : map).Add(point);
            open.Push((entry.Level, point));
            order++;
        }

        int depth = navigation.Count == 0 ? 1 : navigation.Max(e => e.Level) + 1;
        XDocument ncx = new(new XDeclaration("1.0", "UTF-8", null), new XElement(ns + "ncx",
            new XAttribute("version", "2005-1"),
            new XAttribute(XNamespace.Xml + "lang", language),
            new XElement(ns + "head",
                Meta("dtb:uid", identifier), Meta("dtb:depth", depth.ToString(CultureInfo.InvariantCulture)),
                Meta("dtb:totalPageCount", "0"), Meta("dtb:maxPageNumber", "0")),
            new XElement(ns + "docTitle", new XElement(ns + "text", title)),
            map));
        return Serialize(ncx);

        XElement Meta(string name, string content) => new(ns + "meta", new XAttribute("name", name), new XAttribute("content", content));
    }

    private string PackageDocument(List<Chapter> chapters, Resource? cover, List<NavEntry> navigation, string title)
    {
        XNamespace opf = EpubPackage.Opf, dc = EpubPackage.Dc;
        DocumentProperties properties = document.Properties;
        XElement metadata = new(opf + "metadata", new XAttribute(XNamespace.Xmlns + "dc", dc.NamespaceName),
            new XElement(dc + "identifier", new XAttribute("id", "pub-id"), identifier),
            new XElement(dc + "title", title),
            new XElement(dc + "language", language));
        foreach (string creator in Split(properties.Author))
        {
            metadata.Add(new XElement(dc + "creator", Clean(creator)));
        }

        foreach (string subject in Split(properties.Keywords))
        {
            metadata.Add(new XElement(dc + "subject", Clean(subject)));
        }

        if (properties.Comments is { Length: > 0 } description)
        {
            metadata.Add(new XElement(dc + "description", Clean(description)));
        }

        if (properties.GetCustom("Publisher") is { Length: > 0 } publisher)
        {
            metadata.Add(new XElement(dc + "publisher", Clean(publisher)));
        }

        if (properties.GetCustom("Rights") is { Length: > 0 } rights)
        {
            metadata.Add(new XElement(dc + "rights", Clean(rights)));
        }

        foreach (string contributor in Split(properties.GetCustom("Contributors")))
        {
            metadata.Add(new XElement(dc + "contributor", Clean(contributor)));
        }

        if (properties.Created is { } created)
        {
            metadata.Add(new XElement(dc + "date", created.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        DateTimeOffset modified = options.Modified ?? properties.Saved ?? DateTimeOffset.UtcNow;
        metadata.Add(Property("dcterms:modified", modified.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));

        // Accessibility metadata from what the book has (EPUB Accessibility 1.1 5, design Q8).
        bool hasPictures = pictures || cover is not null;
        metadata.Add(Property("schema:accessMode", "textual"));
        if (hasPictures)
        {
            metadata.Add(Property("schema:accessMode", "visual"));
        }

        metadata.Add(Property("schema:accessModeSufficient", "textual"));
        if (navigation.Count > 0)
        {
            metadata.Add(Property("schema:accessibilityFeature", "structuralNavigation"));
        }

        metadata.Add(Property("schema:accessibilityFeature", "tableOfContents"));
        metadata.Add(Property("schema:accessibilityFeature", "readingOrder"));
        if (pictures && !picturesWithoutText)
        {
            metadata.Add(Property("schema:accessibilityFeature", "alternativeText"));
        }

        metadata.Add(Property("schema:accessibilityHazard", "none"));
        metadata.Add(Property("schema:accessibilitySummary", options.AccessibilitySummary ?? Summary(hasPictures)));
        if (cover is not null)
        {
            // EPUB 2 readers find the cover by this meta.
            metadata.Add(new XElement(opf + "meta", new XAttribute("name", "cover"), new XAttribute("content", cover.Id)));
        }

        XElement manifest = new(opf + "manifest",
            Item("nav", "nav.xhtml", "application/xhtml+xml", "nav"),
            Item("ncx", "toc.ncx", "application/x-dtbncx+xml", null));
        if (coverPage)
        {
            manifest.Add(Item("cover", "cover.xhtml", "application/xhtml+xml", null));
        }

        foreach (Chapter chapter in chapters)
        {
            manifest.Add(Item(chapter.Id, chapter.Name, "application/xhtml+xml", null));
        }

        foreach (Resource resource in resources)
        {
            manifest.Add(Item(resource.Id, resource.Name, resource.MediaType, resource.Properties is "cover-image" ? "cover-image" : null));
        }

        XElement spine = new(opf + "spine", new XAttribute("toc", "ncx"));
        if (document.Extensions.Get<EpubBookExtension>(EpubBookExtension.Format)?.Direction is { } direction && direction is "rtl" or "ltr")
        {
            spine.Add(new XAttribute("page-progression-direction", direction));
        }

        if (coverPage)
        {
            spine.Add(new XElement(opf + "itemref", new XAttribute("idref", "cover"), new XAttribute("linear", "yes")));
        }

        foreach (Chapter chapter in chapters)
        {
            spine.Add(new XElement(opf + "itemref", new XAttribute("idref", chapter.Id)));
        }

        // The navigation document is in the spine out of the reading order, so the landmarks and guide may link to it.
        spine.Add(new XElement(opf + "itemref", new XAttribute("idref", "nav"), new XAttribute("linear", "no")));

        // The guide is EPUB 2's and deprecated in EPUB 3, but old readers still use it (design Q2).
        XElement guide = new(opf + "guide", new XElement(opf + "reference", new XAttribute("type", "toc"), new XAttribute("title", "Contents"), new XAttribute("href", "nav.xhtml")));
        if (coverPage)
        {
            guide.AddFirst(new XElement(opf + "reference", new XAttribute("type", "cover"), new XAttribute("title", "Cover"), new XAttribute("href", "cover.xhtml")));
        }

        if (chapters.FirstOrDefault(c => !c.Cover) is { } first)
        {
            guide.Add(new XElement(opf + "reference", new XAttribute("type", "text"), new XAttribute("title", "Start"), new XAttribute("href", first.Name)));
        }

        XDocument package = new(new XDeclaration("1.0", "UTF-8", null), new XElement(opf + "package",
            new XAttribute("version", "3.0"),
            new XAttribute("unique-identifier", "pub-id"),
            new XAttribute(XNamespace.Xml + "lang", language),
            metadata, manifest, spine, guide));
        return Serialize(package);

        XElement Property(string property, string value) => new(opf + "meta", new XAttribute("property", property), value);

        XElement Item(string id, string href, string type, string? itemProperties)
        {
            XElement item = new(opf + "item", new XAttribute("id", id), new XAttribute("href", href), new XAttribute("media-type", type));
            if (itemProperties is not null)
            {
                item.Add(new XAttribute("properties", itemProperties));
            }

            return item;
        }
    }

    private static string Summary(bool hasPictures) => hasPictures
        ? "This book has a table of contents and structured headings; its pictures carry the alternative text the document gave them."
        : "This book has a table of contents and structured headings, and holds text only.";

    private static string[] Split(string? list) =>
        list is null ? [] : list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Serialize(XDocument xml)
    {
        using StringWriter writer = new Utf8StringWriter();
        xml.Save(writer, SaveOptions.None);
        return writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>Text with the characters XML cannot hold (controls, lone surrogates, U+FFFE and U+FFFF) left out.</summary>
    private static string Clean(string text)
    {
        StringBuilder clean = new(text.Length);
        foreach (Rune rune in text.EnumerateRunes())
        {
            if ((rune.Value >= 0x20 || rune.Value is 0x09 or 0x0A or 0x0D) && rune.Value is not (0xFFFE or 0xFFFF))
            {
                clean.Append(rune.ToString());
            }
        }

        return clean.ToString();
    }

    private static string Text(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Attribute(string text) => Text(text).Replace("\"", "&quot;", StringComparison.Ordinal);

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter()
            : base(CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
