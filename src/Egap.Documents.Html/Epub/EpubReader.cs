using System.Globalization;
using System.Text;

using Egami.Css;

namespace Egap.Documents.Html;

/// <summary>What the EPUB reader keeps of the book on the document, so the EPUB writer can keep its identity.</summary>
internal sealed record EpubBookExtension(string Version, string UniqueIdentifier, string? Direction, bool FixedLayout)
{
    public const string Format = "epub";
}

/// <summary>What the EPUB reader keeps of a chapter on its section: the file it came from and its manifest id.</summary>
internal sealed record EpubChapterExtension(string Name, string Id, IReadOnlyList<string> Properties)
{
    public const string Format = "epub";
}

/// <summary>
/// An EPUB book read into one document (Egap 6.6 design Q3): the cover on a first section of its own, then a section with a
/// page break per spine item read through the HTML reader with the book's style sheets, links between chapters as links
/// inside the document, the table of contents as outline levels, metadata as document properties, embedded fonts loaded
/// (design Q5) and fixed-layout pages read at their viewport's size (design Q7).
/// </summary>
internal sealed class EpubReader
{
    /// <summary>The address chapters are read at, so their relative links resolve inside the container and nowhere else.</summary>
    internal static readonly Uri BookRoot = new("https://epub.invalid/");

    private readonly EpubContainer container;
    private readonly EpubPackage package;
    private readonly EpubLoadOptions options;
    private readonly Document document = new();
    private readonly Dictionary<(ConversionWarningKind, string), int> warnings = [];
    private readonly HashSet<string> bookmarkNames = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Chapter, string Id), string> bookmarks = [];
    private readonly Dictionary<string, Section> chapters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BookmarkStart> starts = new(StringComparer.Ordinal);

    private EpubReader(EpubContainer container, EpubPackage package, EpubLoadOptions options)
    {
        this.container = container;
        this.package = package;
        this.options = options;
    }

    public static Document Read(Stream stream, EpubLoadOptions options, out IReadOnlyList<ConversionWarning> warnings)
    {
        EpubContainer container = EpubContainer.Read(stream, options.Limits);
        EpubPackage package = EpubPackage.Read(container);
        container.ReadEncryption(package.UniqueIdentifier, package.Identifiers);
        EpubReader reader = new(container, package, options);
        reader.ReadBook();
        warnings = [.. reader.warnings.Select(w => new ConversionWarning(w.Key.Item1, w.Key.Item2, w.Value))];
        return reader.document;
    }

    private void ReadBook()
    {
        ReadProperties();
        ReadFonts();
        WarnMedia();

        foreach (EpubSpineItem item in package.Spine)
        {
            // The navigation document out of the reading order is the table of contents, read as the outline below.
            if (!(item.Item.Has("nav") && !item.Linear))
            {
                ReadChapter(item);
            }
        }

        // The cover on a first section of its own, unless the first chapter already shows it; a cover page whose picture
        // the HTML reader could not show (an SVG wrapper) gets the picture instead.
        if (package.CoverImage is { } cover && container.Get(cover) is { } coverBytes
            && !(document.Sections.Count > 0 && document.Sections[0].Descendants<Picture>().Any(p => p.Image.Bytes.Span.SequenceEqual(coverBytes))))
        {
            Section? first = document.Sections.Count > 0 ? document.Sections[0] : null;
            bool wrapper = first?.Extensions.Get<EpubChapterExtension>(EpubChapterExtension.Format) is { } chapter
                && first.GetText().Trim().Length == 0 && !first.Descendants<Picture>().Any()
                && container.Get(chapter.Name) is { } page && Encoding.UTF8.GetString(page).Contains(Path.GetFileName(cover), StringComparison.Ordinal);
            AddCover(cover, coverBytes, wrapper ? first : null);
        }

        if (document.Sections.Count == 0)
        {
            document.AddSection();
        }

        LinkChapters();
        ReadNavigation();

        // The package's metadata wins over what the chapters' heads said.
        ReadProperties();
        document.Extensions.Set(EpubBookExtension.Format, new EpubBookExtension(package.Version, package.UniqueIdentifier, package.Direction, package.FixedLayout));
    }

    private void Warn(ConversionWarningKind kind, string message, int count = 1)
    {
        warnings[(kind, message)] = warnings.GetValueOrDefault((kind, message)) + count;
    }

    // ---- Metadata ------------------------------------------------------------------------------------------------------

    private void ReadProperties()
    {
        DocumentProperties properties = document.Properties;
        if (package.Values("title").FirstOrDefault() is { } title)
        {
            properties.Title = title;
        }

        List<string> creators = [.. package.Values("creator")];
        if (creators.Count > 0)
        {
            properties.Author = string.Join("; ", creators);
        }

        List<string> subjects = [.. package.Values("subject")];
        if (subjects.Count > 0)
        {
            properties.Keywords = string.Join("; ", subjects);
        }

        if (package.Values("description").FirstOrDefault() is { } description)
        {
            properties.Comments = EpubPackage.Collapse(description);
        }

        if (package.Values("date").FirstOrDefault() is { } date && ParseDate(date) is { } created)
        {
            properties.Created = created;
        }

        if (package.Meta("dcterms:modified") is { } modified && ParseDate(modified) is { } saved)
        {
            properties.Saved = saved;
        }

        SetCustom("Publisher", package.Values("publisher").FirstOrDefault());
        SetCustom("Rights", package.Values("rights").FirstOrDefault());
        SetCustom("Identifier", package.UniqueIdentifier.Length > 0 ? package.UniqueIdentifier : null);
        if (package.Values("contributor").ToList() is { Count: > 0 } contributors)
        {
            SetCustom("Contributors", string.Join("; ", contributors));
        }

        if (package.Values("language").FirstOrDefault() is { } language)
        {
            document.Styles.DefaultRunFormat.Language = language;
        }

        void SetCustom(string name, string? value)
        {
            if (value is not null)
            {
                properties.SetCustom(name, value);
            }
        }
    }

    private static DateTimeOffset? ParseDate(string text)
    {
        text = text.Trim();
        string[] formats = ["yyyy", "yyyy-MM", "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "yyyy-MM-ddTHH:mmK"];
        return DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value) ? value
            : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value) ? value : null;
    }

    // ---- Fonts and media -----------------------------------------------------------------------------------------------

    /// <summary>The fonts the book's style sheets declare with @font-face, read from the container (design Q5).</summary>
    private void ReadFonts()
    {
        HashSet<string> loaded = new(StringComparer.Ordinal);
        foreach (EpubItem sheet in package.Manifest.Values.Where(i => i.MediaType == "text/css"))
        {
            if (container.Get(sheet.Name) is not { } bytes)
            {
                continue;
            }

            CssStyleSheet parsed;
            try
            {
                parsed = CssStyleSheet.Parse(Encoding.UTF8.GetString(bytes));
            }
            catch (FormatException)
            {
                continue;
            }

            foreach (CssFontFaceRule rule in parsed.Rules.OfType<CssFontFaceRule>())
            {
                CssFontFace face = rule.FontFace;
                if (face.Family is not { Length: > 0 } family)
                {
                    continue;
                }

                foreach (CssFontSource source in face.Sources)
                {
                    if (source.Url is not { } url || EpubContainer.Resolve(sheet.Name, url, out _) is not { } name || container.Get(name) is not { } font)
                    {
                        continue;
                    }

                    bool bold = face.Weight is { } weight && weight.Min >= 600;
                    bool italic = face.Style is "italic" or "oblique";
                    if (loaded.Add($"{family}\u0000{bold}\u0000{italic}"))
                    {
                        document.EmbeddedFonts.Add(new EmbeddedFont(family, bold, italic, font));
                    }

                    break;
                }
            }
        }
    }

    /// <summary>Audio, video, media overlays and scripts are left out with a warning (design Q7).</summary>
    private void WarnMedia()
    {
        int media = package.Manifest.Values.Count(i => i.MediaType.StartsWith("audio/", StringComparison.Ordinal)
            || i.MediaType.StartsWith("video/", StringComparison.Ordinal) || i.MediaType == "application/smil+xml");
        if (media > 0)
        {
            Warn(ConversionWarningKind.MediaDropped, "Audio, video and media overlays", media);
        }

        int scripted = package.Manifest.Values.Count(i => i.Has("scripted") || i.MediaType is "application/javascript" or "text/javascript" or "application/ecmascript");
        if (scripted > 0)
        {
            Warn(ConversionWarningKind.MediaDropped, "Scripts, which Egap never runs", scripted);
        }
    }

    // ---- Chapters ------------------------------------------------------------------------------------------------------

    private void AddCover(string name, byte[] bytes, Section? into)
    {
        Section section = into ?? document.AddSection();
        if (into is null)
        {
            document.Sections.Remove(section);
            document.Sections.Insert(0, section);
        }
        else
        {
            section.Body.Blocks.Clear();
        }

        section.PageSetup.SectionStart = SectionStart.NewPage;
        double pageWidth = section.PageSetup.PageWidth ?? 612, pageHeight = section.PageSetup.PageHeight ?? 792;
        double width = pageWidth - (section.PageSetup.MarginLeft ?? 72) - (section.PageSetup.MarginRight ?? 72);
        double height = pageHeight - (section.PageSetup.MarginTop ?? 72) - (section.PageSetup.MarginBottom ?? 72) - 24;
        (double w, double h) = ImageSize.Intrinsic(bytes);
        double scale = w > 0 && h > 0 ? Math.Min(width / w, height / h) : 1;
        Paragraph paragraph = section.Body.AddParagraph();
        paragraph.Format.Alignment = ParagraphAlignment.Center;
        string type = package.Manifest.Values.FirstOrDefault(i => i.Name == name)?.MediaType ?? "image/jpeg";
        paragraph.Inlines.Add(new Picture(document, new ImageData(bytes, type), Math.Max(1, w * scale), Math.Max(1, h * scale))
        {
            Description = "Cover",
            SourceUrl = name,
        });
        EpubChapterExtension? kept = section.Extensions.Get<EpubChapterExtension>(EpubChapterExtension.Format);
        section.Extensions.Set(EpubChapterExtension.Format, new EpubChapterExtension(kept?.Name ?? name, kept?.Id ?? "cover-image", ["cover"]));
    }

    private void ReadChapter(EpubSpineItem spine)
    {
        EpubItem item = spine.Item;
        byte[]? bytes = container.Get(item.Name);
        if (bytes is null)
        {
            Warn(ConversionWarningKind.ResourceNotLoaded, "Chapters the book lists but does not hold");
            return;
        }

        bool image = item.MediaType.StartsWith("image/", StringComparison.Ordinal);
        if (!item.IsXhtml && !image)
        {
            Warn(ConversionWarningKind.FormattingDropped, "Spine items that are neither XHTML nor pictures");
            return;
        }

        List<string> userSheets = [.. options.UserStyleSheets];
        bool fixedLayout = spine.Properties.Contains("rendition:layout-pre-paginated")
            || (package.FixedLayout && !spine.Properties.Contains("rendition:layout-reflowable"));
        HtmlDocument dom;
        if (image)
        {
            // A picture in the spine (an SVG page, a comic's PNG) is shown as a picture filling its page.
            dom = HtmlParsing.Parse($"<!DOCTYPE html><html><body><img src=\"{Uri.EscapeDataString(Path.GetFileName(item.Name))}\" alt=\"\"></body></html>");
        }
        else
        {
            dom = XhtmlText.Parse(bytes, options.Limits);
        }

        if (fixedLayout && Viewport(dom, bytes) is { } viewport)
        {
            // A fixed-layout page is its viewport, laid out with no margins (EPUB 3.3 8.2.2).
            userSheets.Add(string.Create(CultureInfo.InvariantCulture, $"@page {{ size: {viewport.Width}px {viewport.Height}px; margin: 0; }} html, body {{ margin: 0; }}"));
        }

        HtmlLoadOptions chapter = new()
        {
            BaseUri = new Uri(BookRoot, string.Join('/', item.Name.Split('/').Select(Uri.EscapeDataString))),
            ResourceResolver = Resolve,
            Media = options.Media,
            UserStyleSheets = userSheets,
            Limits = options.Limits,
        };

        int before = document.Sections.Count;
        HtmlReader.Read(dom, chapter, document, out IReadOnlyList<ConversionWarning> chapterWarnings);
        foreach (ConversionWarning warning in chapterWarnings)
        {
            Warn(warning.Kind, warning.Message, warning.Count);
        }

        for (int i = before; i < document.Sections.Count; i++)
        {
            Section section = document.Sections[i];
            section.PageSetup.SectionStart = SectionStart.NewPage;
            if (i == before)
            {
                section.Extensions.Set(EpubChapterExtension.Format, new EpubChapterExtension(item.Name, item.Id, spine.Properties));
                chapters.TryAdd(item.Name, section);
            }

            KeepBookmarks(section, item.Name);
        }
    }

    /// <summary>
    /// A fixed-layout page's viewport in CSS pixels: the meta viewport's width and height (EPUB 3.3 8.2.2.2), else an SVG
    /// page's viewBox.
    /// </summary>
    private static (double Width, double Height)? Viewport(HtmlDocument dom, byte[] bytes)
    {
        foreach (HtmlElement meta in dom.Descendants().OfType<HtmlElement>().Where(e => e.IsHtml("meta")))
        {
            if (!string.Equals(meta.GetAttribute("name", null), "viewport", StringComparison.OrdinalIgnoreCase) || meta.GetAttribute("content", null) is not { } content)
            {
                continue;
            }

            double? width = null, height = null;
            foreach (string part in content.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
                if (pair.Length == 2 && double.TryParse(pair[1].Replace("px", "", StringComparison.OrdinalIgnoreCase), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0)
                {
                    if (pair[0].Equals("width", StringComparison.OrdinalIgnoreCase))
                    {
                        width = value;
                    }
                    else if (pair[0].Equals("height", StringComparison.OrdinalIgnoreCase))
                    {
                        height = value;
                    }
                }
            }

            if (width is { } w && height is { } h)
            {
                return (Math.Min(w, 20000), Math.Min(h, 20000));
            }
        }

        string text = Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 4096)));
        int viewBox = text.IndexOf("viewBox=\"", StringComparison.Ordinal);
        if (viewBox >= 0 && text.IndexOf('"', viewBox + 9) is > 0 and var end)
        {
            string[] numbers = text[(viewBox + 9)..end].Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            if (numbers.Length == 4 && double.TryParse(numbers[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
                && double.TryParse(numbers[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && w > 0 && h > 0)
            {
                return (Math.Min(w, 20000), Math.Min(h, 20000));
            }
        }

        return null;
    }

    /// <summary>A chapter's links: entries of the container by their address, the caller's resolver for anything outside it.</summary>
    private byte[]? Resolve(Uri uri)
    {
        if (uri.Host == BookRoot.Host && uri.Scheme == BookRoot.Scheme)
        {
            string name = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            return container.Get(name);
        }

        return options.ResourceResolver?.Invoke(uri);
    }

    /// <summary>
    /// Keeps a chapter's ids as bookmarks, renaming one an earlier chapter already used, and notes where each went so links
    /// across chapters find it.
    /// </summary>
    private void KeepBookmarks(Section section, string chapter)
    {
        foreach (BookmarkStart start in section.Descendants<BookmarkStart>().ToList())
        {
            string id = start.Name;
            if (bookmarks.ContainsKey((chapter, id)))
            {
                continue;
            }

            string name = id;
            if (!bookmarkNames.Add(name))
            {
                string stem = Sanitize(Path.GetFileNameWithoutExtension(chapter));
                name = stem + "_" + id;
                for (int n = 2; !bookmarkNames.Add(name); n++)
                {
                    name = stem + "_" + id + "_" + n.ToString(CultureInfo.InvariantCulture);
                }

                start.Name = name;
            }

            bookmarks[(chapter, id)] = name;
            starts[name] = start;
        }
    }

    private static string Sanitize(string text)
    {
        StringBuilder clean = new(text.Length);
        foreach (char c in text)
        {
            clean.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return clean.Length == 0 || !char.IsAsciiLetter(clean[0]) ? "c" + clean : clean.ToString();
    }

    /// <summary>The bookmark a target (a chapter file and an id in it, or the chapter's start) reads as, adding one when needed.</summary>
    private string? BookmarkFor(string chapter, string? fragment)
    {
        if (fragment is { Length: > 0 } && bookmarks.TryGetValue((chapter, fragment), out string? name))
        {
            return name;
        }

        if (!chapters.TryGetValue(chapter, out Section? section))
        {
            return null;
        }

        if (bookmarks.TryGetValue((chapter, ""), out name))
        {
            return name;
        }

        Paragraph? first = section.Body.Descendants<Paragraph>().FirstOrDefault();
        if (first is null)
        {
            return null;
        }

        name = Sanitize(Path.GetFileNameWithoutExtension(chapter));
        for (int n = 2; !bookmarkNames.Add(name); n++)
        {
            name = Sanitize(Path.GetFileNameWithoutExtension(chapter)) + "_" + n.ToString(CultureInfo.InvariantCulture);
        }

        BookmarkStart start = new(document, name);
        first.Inlines.Insert(0, start);
        bookmarks[(chapter, "")] = name;
        starts[name] = start;
        return name;
    }

    /// <summary>Links from one chapter to another (chapter2.xhtml#note) become links to the place inside the document.</summary>
    private void LinkChapters()
    {
        foreach (Section section in document.Sections)
        {
            if (section.Extensions.Get<EpubChapterExtension>(EpubChapterExtension.Format) is not { } chapter)
            {
                continue;
            }

            int index = document.Sections.IndexOf(section);
            for (int i = index; i < document.Sections.Count; i++)
            {
                Section part = document.Sections[i];
                if (i > index && part.Extensions.Get<EpubChapterExtension>(EpubChapterExtension.Format) is not null)
                {
                    break;
                }

                foreach (Hyperlink link in part.Descendants<Hyperlink>())
                {
                    if (link.Address is { } address && EpubContainer.Resolve(chapter.Name, address, out string? fragment) is { } target
                        && chapters.ContainsKey(target) && BookmarkFor(target, fragment) is { } name)
                    {
                        link.Address = null;
                        link.Anchor = name;
                    }
                    else if (link.Address is null && link.Anchor is { } anchor && bookmarks.TryGetValue((chapter.Name, anchor), out string? renamed))
                    {
                        link.Anchor = renamed;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The table of contents as the outline: each entry's target paragraph gets the entry's outline level unless it is a
    /// heading already, so PDF bookmarks and tables of contents follow the book's own.
    /// </summary>
    private void ReadNavigation()
    {
        foreach (EpubNavEntry entry in package.ReadNavigation(container, out _))
        {
            if (entry.Target is null || BookmarkFor(entry.Target, entry.Fragment) is not { } name)
            {
                continue;
            }

            if (starts.GetValueOrDefault(name)?.Ancestors().OfType<Paragraph>().FirstOrDefault() is not { } paragraph)
            {
                continue;
            }

            // An empty anchor paragraph (a bare <a id>) marks the paragraph after it.
            while (paragraph.GetText().Trim().Length == 0 && paragraph.NextSibling as Paragraph is { } next)
            {
                paragraph = next;
            }

            if (paragraph.GetEffectiveFormat().OutlineLevel >= 9)
            {
                paragraph.Format.OutlineLevel = Math.Min(entry.Level, 8);
            }
        }
    }
}
