using System.IO.Compression;
using System.Text;

namespace Egap.Documents.Html.Tests;

/// <summary>EPUB 2 and 3 books read into the model and written from it.</summary>
public class EpubTests
{
    private const string Opf3 = """
        <?xml version="1.0" encoding="UTF-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="uid">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:identifier id="uid">urn:uuid:12345678-1234-5678-1234-567812345678</dc:identifier>
            <dc:title>The Test Book</dc:title>
            <dc:creator>Ada Writer</dc:creator>
            <dc:creator>Bo Helper</dc:creator>
            <dc:language>nb</dc:language>
            <dc:subject>Tests</dc:subject>
            <dc:description>A book for tests.</dc:description>
            <dc:publisher>Egap Press</dc:publisher>
            <dc:date>2020-05-17</dc:date>
            <meta property="dcterms:modified">2024-01-02T03:04:05Z</meta>
          </metadata>
          <manifest>
            <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
            <item id="css" href="css/book.css" media-type="text/css"/>
            <item id="cover" href="images/cover.png" media-type="image/png" properties="cover-image"/>
            <item id="c1" href="text/one.xhtml" media-type="application/xhtml+xml"/>
            <item id="c2" href="text/two.xhtml" media-type="application/xhtml+xml"/>
            <item id="audio" href="media/a.mp3" media-type="audio/mpeg"/>
          </manifest>
          <spine>
            <itemref idref="c1"/>
            <itemref idref="c2"/>
          </spine>
        </package>
        """;

    private const string Nav = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
        <head><title>Contents</title></head>
        <body><nav epub:type="toc"><ol>
          <li><a href="text/one.xhtml">Part One</a><ol><li><a href="text/one.xhtml#s1">A section</a></li></ol></li>
          <li><a href="text/two.xhtml#top">Part Two</a></li>
        </ol></nav></body></html>
        """;

    private const string One = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><title>One</title><link rel="stylesheet" href="../css/book.css"/></head>
        <body>
          <p class="title">Part One</p>
          <p>See <a href="two.xhtml#note">the note</a> and <a href="#s1">below</a>.</p>
          <a id="s1"/>
          <p class="big">A section</p>
          <p id="dup">First dup.</p>
        </body></html>
        """;

    private const string Two = """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><title>Two</title></head>
        <body>
          <h1 id="top">Part Two</h1>
          <p id="note">The note&#160;here.</p>
          <p id="dup">Second dup.</p>
          <p><img src="../images/cover.png" alt="dot"/></p>
        </body></html>
        """;

    internal static byte[] Book(params (string Name, string Text)[] files)
    {
        return BookBytes(files.Select(f => (f.Name, Encoding.UTF8.GetBytes(f.Text))).ToArray());
    }

    internal static byte[] BookBytes(params (string Name, byte[] Data)[] files)
    {
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry mimetype = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (Stream s = mimetype.Open())
            {
                s.Write("application/epub+zip"u8);
            }

            if (!files.Any(f => f.Name == "META-INF/container.xml"))
            {
                Add(zip, "META-INF/container.xml", Encoding.UTF8.GetBytes("""
                    <?xml version="1.0"?>
                    <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                      <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
                    </container>
                    """));
            }

            foreach ((string name, byte[] data) in files)
            {
                Add(zip, name, data);
            }
        }

        return stream.ToArray();

        static void Add(ZipArchive zip, string name, byte[] data)
        {
            using Stream s = zip.CreateEntry(name).Open();
            s.Write(data);
        }
    }

    private static byte[] TestBook() => BookBytes(
        ("OEBPS/content.opf", Encoding.UTF8.GetBytes(Opf3)),
        ("OEBPS/nav.xhtml", Encoding.UTF8.GetBytes(Nav)),
        ("OEBPS/css/book.css", Encoding.UTF8.GetBytes(".big { font-size: 20pt; } .title { font-weight: bold; }")),
        ("OEBPS/images/cover.png", Convert.FromBase64String(MhtmlTests.Png)),
        ("OEBPS/text/one.xhtml", Encoding.UTF8.GetBytes(One)),
        ("OEBPS/text/two.xhtml", Encoding.UTF8.GetBytes(Two)));

    [Fact]
    public void Epub3BooksReadIntoOneDocumentWithASectionPerChapter()
    {
        Document document = EpubFormat.Load(new MemoryStream(TestBook()), out IReadOnlyList<ConversionWarning> warnings);

        // The cover is not shown by the first chapter, so it gets a section of its own.
        Assert.Equal(3, document.Sections.Count);
        Assert.Single(document.Sections[0].Descendants<Picture>());
        Assert.All(document.Sections.Skip(1), s => Assert.Equal(SectionStart.NewPage, s.PageSetup.SectionStart));
        string text = document.GetText();
        Assert.Contains("The note here.", text, StringComparison.Ordinal);
        Assert.Contains("Second dup.", text, StringComparison.Ordinal);
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.MediaDropped);
    }

    [Fact]
    public void MetadataBecomesDocumentProperties()
    {
        Document document = EpubFormat.Load(new MemoryStream(TestBook()));

        Assert.Equal("The Test Book", document.Properties.Title);
        Assert.Equal("Ada Writer; Bo Helper", document.Properties.Author);
        Assert.Equal("Tests", document.Properties.Keywords);
        Assert.Equal("A book for tests.", document.Properties.Comments);
        Assert.Equal("Egap Press", document.Properties.GetCustom("Publisher"));
        Assert.Equal(new DateTimeOffset(2020, 5, 17, 0, 0, 0, TimeSpan.Zero), document.Properties.Created);
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero), document.Properties.Saved);
        Assert.Equal("nb", document.Styles.DefaultRunFormat.Language);
    }

    [Fact]
    public void LinksAcrossChaptersPointInsideTheDocument()
    {
        Document document = EpubFormat.Load(new MemoryStream(TestBook()));

        List<Hyperlink> links = [.. document.Descendants<Hyperlink>()];
        Hyperlink note = links.Single(l => l.GetText() == "the note");
        Assert.Null(note.Address);
        Assert.NotNull(document.GetBookmark(note.Anchor!));
        Assert.Contains("The note", document.GetBookmark(note.Anchor!)!.Start.Ancestors().OfType<Paragraph>().First().GetText(), StringComparison.Ordinal);
        Assert.Equal("s1", links.Single(l => l.GetText() == "below").Anchor);

        // The second chapter's "dup" is renamed, as the first chapter has one.
        string[] names = [.. document.GetBookmarks().Select(b => b.Name)];
        Assert.Contains("dup", names);
        Assert.Contains("two_dup", names);
    }

    [Fact]
    public void TheTableOfContentsBecomesTheOutline()
    {
        Document document = EpubFormat.Load(new MemoryStream(TestBook()));

        Paragraph partOne = document.Descendants<Paragraph>().First(p => p.GetText() == "Part One");
        Paragraph section = document.Descendants<Paragraph>().First(p => p.GetText() == "A section");
        Assert.Equal(0, partOne.GetEffectiveFormat().OutlineLevel);
        Assert.Equal(1, section.GetEffectiveFormat().OutlineLevel);
    }

    [Fact]
    public void Epub2BooksReadTheirNcx()
    {
        const string opf = """
            <?xml version="1.0"?>
            <!DOCTYPE package PUBLIC "+//ISBN 0-9673008-1-9//DTD OEB 1.2 Package//EN" "http://openebook.org/dtds/oeb-1.2/oebpkg12.dtd">
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:opf="http://www.idpf.org/2007/opf">
                <dc:title>Old Book</dc:title><dc:identifier id="id">isbn-1</dc:identifier><dc:language>en</dc:language>
              </metadata>
              <manifest>
                <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                <item id="ch" href="ch.html" media-type="application/xhtml+xml"/>
              </manifest>
              <spine toc="ncx"><itemref idref="ch"/></spine>
            </package>
            """;
        const string ncx = """
            <?xml version="1.0"?>
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><head/><docTitle><text>Old</text></docTitle>
            <navMap><navPoint id="a" playOrder="1"><navLabel><text>Chapter I</text></navLabel><content src="ch.html#c1"/></navPoint></navMap></ncx>
            """;
        const string chapter = """
            <?xml version="1.0" encoding="utf-8"?>
            <!DOCTYPE html PUBLIC "-//W3C//DTD XHTML 1.1//EN" "http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd">
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>x</title></head>
            <body><div id="c1" class="chapter">Chapter I</div><p>It was&nbsp;dark &mdash; and stormy.</p></body></html>
            """;

        Document document = EpubFormat.Load(new MemoryStream(Book(("OEBPS/content.opf", opf), ("OEBPS/toc.ncx", ncx), ("OEBPS/ch.html", chapter))));

        Assert.Equal("Old Book", document.Properties.Title);
        Assert.Contains("It was dark — and stormy.", document.GetText(), StringComparison.Ordinal);
        Assert.Equal(0, document.Descendants<Paragraph>().First(p => p.GetText() == "Chapter I").GetEffectiveFormat().OutlineLevel);
    }

    [Fact]
    public void DrmBooksAreRefusedNamingTheScheme()
    {
        const string encryption = """
            <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
              <enc:EncryptedData><enc:EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes128-cbc"/>
              <enc:CipherData><enc:CipherReference URI="OEBPS/text/one.xhtml"/></enc:CipherData></enc:EncryptedData>
            </encryption>
            """;
        byte[] book = BookBytes(
            ("OEBPS/content.opf", Encoding.UTF8.GetBytes(Opf3)),
            ("OEBPS/text/one.xhtml", Encoding.UTF8.GetBytes(One)),
            ("META-INF/encryption.xml", Encoding.UTF8.GetBytes(encryption)),
            ("META-INF/rights.xml", Encoding.UTF8.GetBytes("<adept:rights xmlns:adept=\"http://ns.adobe.com/adept\"/>")));

        DocumentException error = Assert.Throws<DocumentException>(() => EpubFormat.Load(new MemoryStream(book)));

        Assert.Contains("Adobe ADEPT", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ObfuscatedFontsAreReadBack()
    {
        byte[] font = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "files", "fonts", "NotoSans-Latin.ttf"));
        const string uid = "urn:uuid:12345678-1234-5678-1234-567812345678";
        byte[] obfuscated = EpubContainer.Deobfuscate(font, "idpf:" + uid);
        Assert.NotEqual(font.AsSpan(0, 16).ToArray(), obfuscated.AsSpan(0, 16).ToArray());
        string opf = Opf3.Replace("<item id=\"css\"", "<item id=\"font\" href=\"fonts/f.ttf\" media-type=\"font/ttf\"/><item id=\"css\"", StringComparison.Ordinal);
        const string encryption = """
            <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
              <enc:EncryptedData><enc:EncryptionMethod Algorithm="http://www.idpf.org/2008/embedding"/>
              <enc:CipherData><enc:CipherReference URI="OEBPS/fonts/f.ttf"/></enc:CipherData></enc:EncryptedData>
            </encryption>
            """;
        byte[] book = BookBytes(
            ("OEBPS/content.opf", Encoding.UTF8.GetBytes(opf)),
            ("OEBPS/css/book.css", "@font-face { font-family: 'Book Sans'; src: url(../fonts/f.ttf); font-weight: bold; }"u8.ToArray()),
            ("OEBPS/fonts/f.ttf", obfuscated),
            ("OEBPS/text/one.xhtml", Encoding.UTF8.GetBytes(One)),
            ("META-INF/encryption.xml", Encoding.UTF8.GetBytes(encryption)));

        Document document = EpubFormat.Load(new MemoryStream(book));

        EmbeddedFont embedded = Assert.Single(document.EmbeddedFonts);
        Assert.Equal("Book Sans", embedded.Family);
        Assert.True(embedded.Bold);
        Assert.Equal(font, embedded.Data.ToArray());
    }

    [Fact]
    public void FixedLayoutPagesTakeTheirViewport()
    {
        string opf = Opf3.Replace("<meta property=\"dcterms:modified\">", "<meta property=\"rendition:layout\">pre-paginated</meta><meta property=\"dcterms:modified\">", StringComparison.Ordinal);
        const string page = """
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml"><head><title>p</title><meta name="viewport" content="width=600, height=800"/></head>
            <body><div style="position: absolute; left: 100px; top: 200px">Placed</div></body></html>
            """;
        byte[] book = BookBytes(
            ("OEBPS/content.opf", Encoding.UTF8.GetBytes(opf)),
            ("OEBPS/text/one.xhtml", Encoding.UTF8.GetBytes(page)),
            ("OEBPS/text/two.xhtml", Encoding.UTF8.GetBytes(page)));

        Document document = EpubFormat.Load(new MemoryStream(book));

        Assert.Contains(document.Descendants<Run>(), r => r.Text.Contains("Placed", StringComparison.Ordinal));
        Section first = document.Sections[0];
        Assert.Equal(450, first.PageSetup.PageWidth!.Value, 1);
        Assert.Equal(600, first.PageSetup.PageHeight!.Value, 1);
        Assert.Equal(0, first.PageSetup.MarginLeft ?? 0, 1);
    }

    [Fact]
    public void PathsOutsideTheContainerAreRefused()
    {
        byte[] book = BookBytes(("OEBPS/content.opf", Encoding.UTF8.GetBytes(Opf3)), ("../evil.xhtml", "x"u8.ToArray()));

        Assert.Throws<DocumentException>(() => EpubFormat.Load(new MemoryStream(book)));
        Assert.Null(EpubContainer.Resolve("OEBPS/a.xhtml", "../../etc/passwd", out _));
        Assert.Null(EpubContainer.Resolve("OEBPS/a.xhtml", "http://example.com/x.png", out _));
        Assert.Equal("OEBPS/images/a b.png", EpubContainer.Resolve("OEBPS/text/a.xhtml", "../images/a%20b.png#frag", out string? fragment));
        Assert.Equal("frag", fragment);
    }

    [Fact]
    public void LimitsHold()
    {
        byte[] book = TestBook();

        Assert.Throws<DocumentLimitException>(() => EpubFormat.Load(new MemoryStream(book), new EpubLoadOptions { Limits = new DocumentLimits { MaxEntries = 3 } }));
        Assert.Throws<DocumentLimitException>(() => EpubFormat.Load(new MemoryStream(book), new EpubLoadOptions { Limits = new DocumentLimits { MaxPartSize = 100 } }));
        Assert.Throws<DocumentLimitException>(() => EpubFormat.Load(new MemoryStream(book), new EpubLoadOptions { Limits = new DocumentLimits { MaxTotalSize = 1000 } }));
        byte[] bomb = BookBytes(("OEBPS/content.opf", Encoding.UTF8.GetBytes(Opf3)), ("OEBPS/big.txt", new byte[3 * 1024 * 1024]));
        Assert.Throws<DocumentLimitException>(() => EpubFormat.Load(new MemoryStream(bomb), new EpubLoadOptions { Limits = new DocumentLimits { MaxCompressionRatio = 10 } }));
    }

    [Fact]
    public void BooksAreIdentifiedAndRegistered()
    {
        HtmlFormat.Register();
        Assert.Equal(DocumentFormatKind.Epub, Document.Identify(new MemoryStream(TestBook())));
        Assert.Equal(DocumentFormatKind.Docx, Document.Identify(new MemoryStream(new Document().ToEpub()[..4].Concat(new byte[100]).ToArray())));
        Document document = Document.Load(new MemoryStream(TestBook()));
        Assert.Equal("The Test Book", document.Properties.Title);
    }

    // ---- Writing ---------------------------------------------------------------------------------------------------------

    private static Document Sample()
    {
        Document document = HtmlFormat.Parse("""
            <html lang="en"><head><title>Written Book</title><meta name="author" content="Cy Author"></head><body>
            <h1>First</h1><p>Opening text with a <a href="#later">link ahead</a>.</p><h2>Sub</h2><p>More &amp; more.</p>
            <h1>Second</h1><p id="later">The later part.</p>
            <p><img src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==" alt="A dot"></p>
            <p><a href="https://example.com/">Outside</a> and <a href="missing.html">nowhere</a>.</p>
            </body></html>
            """);
        document.Properties.Keywords = "one; two";
        return document;
    }

    private static Dictionary<string, string> Entries(byte[] book)
    {
        using ZipArchive zip = new(new MemoryStream(book), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using StreamReader reader = new(e.Open());
            return reader.ReadToEnd();
        });
    }

    [Fact]
    public void WrittenBooksHaveTheContainerEpubNeeds()
    {
        byte[] book = Sample().ToEpub(new EpubSaveOptions { Modified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) });

        Assert.Equal("mimetype", Encoding.ASCII.GetString(book, 30, 8));
        Assert.Equal("application/epub+zip", Encoding.ASCII.GetString(book, 38, 20));
        Dictionary<string, string> entries = Entries(book);
        Assert.Contains("META-INF/container.xml", entries.Keys);
        string opf = entries["OEBPS/content.opf"];
        Assert.Contains("<dc:title>Written Book</dc:title>", opf, StringComparison.Ordinal);
        Assert.Contains("<dc:creator>Cy Author</dc:creator>", opf, StringComparison.Ordinal);
        Assert.Contains($"<dc:language>{Sample().Styles.DefaultRunFormat.Language}</dc:language>", opf, StringComparison.Ordinal);
        Assert.Contains("<dc:subject>two</dc:subject>", opf, StringComparison.Ordinal);
        Assert.Contains("<meta property=\"dcterms:modified\">2026-01-01T00:00:00Z</meta>", opf, StringComparison.Ordinal);
        Assert.Contains("schema:accessibilityFeature\">alternativeText", opf, StringComparison.Ordinal);
        Assert.Contains("schema:accessibilityHazard\">none", opf, StringComparison.Ordinal);
        Assert.Contains("properties=\"nav\"", opf, StringComparison.Ordinal);
        Assert.Contains("toc=\"ncx\"", opf, StringComparison.Ordinal);

        // Split at the two level 1 headings.
        string[] chapters = [.. entries.Keys.Where(k => k.StartsWith("OEBPS/chapter", StringComparison.Ordinal)).Order()];
        Assert.Equal(["OEBPS/chapter001.xhtml", "OEBPS/chapter002.xhtml"], chapters);
        Assert.Contains("href=\"chapter002.xhtml#later\"", entries[chapters[0]], StringComparison.Ordinal);
        Assert.DoesNotContain("missing.html", entries[chapters[1]], StringComparison.Ordinal);
        Assert.Contains("https://example.com/", entries[chapters[1]], StringComparison.Ordinal);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", entries[chapters[0]], StringComparison.Ordinal);
        Assert.Contains("xmlns=\"http://www.w3.org/1999/xhtml\"", entries[chapters[0]], StringComparison.Ordinal);
        Assert.Contains("<img", entries[chapters[1]], StringComparison.Ordinal);
        foreach (string chapter in chapters)
        {
            _ = System.Xml.Linq.XDocument.Parse(entries[chapter]);
        }

        string nav = entries["OEBPS/nav.xhtml"];
        Assert.Contains(">First</a>", nav, StringComparison.Ordinal);
        Assert.Contains(">Sub</a>", nav, StringComparison.Ordinal);
        _ = System.Xml.Linq.XDocument.Parse(nav);
        Assert.Contains("<text>Second</text>", entries["OEBPS/toc.ncx"], StringComparison.Ordinal);
    }

    [Fact]
    public void WrittenBooksReadBack()
    {
        Document document = Sample();
        byte[] book = document.ToEpub();

        Document again = EpubFormat.Load(new MemoryStream(book));

        Assert.Equal("Written Book", again.Properties.Title);
        Assert.Equal("Cy Author", again.Properties.Author);
        Assert.Contains("More & more.", again.GetText(), StringComparison.Ordinal);
        Assert.Single(again.Descendants<Picture>());
        Assert.Equal(2, again.Sections.Count);
        Hyperlink ahead = again.Descendants<Hyperlink>().Single(l => l.GetText() == "link ahead");
        Assert.Equal("later", ahead.Anchor);

        // A book read from EPUB keeps its chapter files and identifier.
        Dictionary<string, string> first = Entries(book), second = Entries(again.ToEpub());
        Assert.Equal(first.Keys.Where(k => k.EndsWith(".xhtml", StringComparison.Ordinal)).Order(), second.Keys.Where(k => k.EndsWith(".xhtml", StringComparison.Ordinal)).Order());
        static string Id(Dictionary<string, string> e) => System.Text.RegularExpressions.Regex.Match(e["OEBPS/content.opf"], "urn:uuid:[0-9a-f-]+").Value;
        Assert.Equal(Id(first), Id(second));
    }

    [Fact]
    public void CoverAndFontsAreWrittenWhenAsked()
    {
        Document document = Sample();
        byte[] font = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "files", "fonts", "NotoSans-Latin.ttf"));
        document.EmbeddedFonts.Add(new EmbeddedFont("Noto Sans", false, false, font));

        Dictionary<string, string> plain = Entries(document.ToEpub());
        Assert.DoesNotContain(plain.Keys, k => k.StartsWith("OEBPS/fonts/", StringComparison.Ordinal));

        byte[] book = document.ToEpub(new EpubSaveOptions { EmbedFonts = true, CoverImage = new ImageData(Convert.FromBase64String(MhtmlTests.Png), "image/png") });
        Dictionary<string, string> entries = Entries(book);
        Assert.Contains("OEBPS/cover.xhtml", entries.Keys);
        Assert.Contains("properties=\"cover-image\"", entries["OEBPS/content.opf"], StringComparison.Ordinal);
        Assert.Contains("http://www.idpf.org/2008/embedding", entries["META-INF/encryption.xml"], StringComparison.Ordinal);

        Document again = EpubFormat.Load(new MemoryStream(book));
        EmbeddedFont read = Assert.Single(again.EmbeddedFonts);
        Assert.True(read.Data.Length < font.Length, "the font is subset");
        Egami.Fonts.Typeface face = Egami.Fonts.Typeface.FromMemory(read.Data);
        Assert.NotEqual(0, face.GetGlyph('O'));
    }

    [Fact]
    public void PicturesInTheSpineAndANonLinearNavAreRead()
    {
        const string opf = """
            <?xml version="1.0"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="u">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="u">x</dc:identifier><dc:title>Comic</dc:title><dc:language>en</dc:language></metadata>
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="p1" href="images/page%201.png" media-type="image/png"/>
              </manifest>
              <spine><itemref idref="p1"/><itemref idref="nav" linear="no"/></spine>
            </package>
            """;
        byte[] book = BookBytes(
            ("OEBPS/content.opf", Encoding.UTF8.GetBytes(opf)),
            ("OEBPS/nav.xhtml", Encoding.UTF8.GetBytes(Nav)),
            ("OEBPS/images/page 1.png", Convert.FromBase64String(MhtmlTests.Png)));

        Document document = EpubFormat.Load(new MemoryStream(book));

        Section page = Assert.Single(document.Sections);
        Assert.Single(page.Descendants<Picture>());
    }

    [Fact]
    public void WrittenSvgPicturesHoldOnlyThemselves()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        byte[] svg = Encoding.UTF8.GetBytes("""
            <?xml-stylesheet href="style.css"?>
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 10 10" onload="go()">
              <script>go()</script><image xlink:href="other.png" width="5" height="5"/><rect width="10" height="10"/>
            </svg>
            """);
        paragraph.Inlines.Add(new Picture(document, new ImageData(svg, "image/svg+xml"), 10, 10) { Description = "Box" });

        Dictionary<string, string> entries = Entries(document.ToEpub());

        string written = entries.Single(e => e.Key.EndsWith(".svg", StringComparison.Ordinal)).Value;
        Assert.Contains("<rect", written, StringComparison.Ordinal);
        Assert.DoesNotContain("script", written, StringComparison.Ordinal);
        Assert.DoesNotContain("other.png", written, StringComparison.Ordinal);
        Assert.DoesNotContain("onload", written, StringComparison.Ordinal);
        Assert.DoesNotContain("style.css", written, StringComparison.Ordinal);
    }

    [Fact]
    public void ABookmarkInsideAParagraphIsWrittenOnce()
    {
        Document document = HtmlFormat.Parse("<p>Start <a id=\"mid\" href=\"#mid\">middle</a> end</p>");

        string html = document.ToHtml();

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "id=\"mid\""));
    }

    [Fact]
    public void SplittingCanBeTurnedOff()
    {
        Dictionary<string, string> entries = Entries(Sample().ToEpub(new EpubSaveOptions { SplitAt = EpubSplit.None }));

        Assert.Single(entries.Keys, k => k.StartsWith("OEBPS/chapter", StringComparison.Ordinal));
    }
}
