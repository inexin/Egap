namespace Egap.Documents.Fuzz;

/// <summary>
/// The fuzz targets for Egap.Documents' readers. Markdown has no invalid input, so its reader must never throw; the DOCX
/// readers may throw only <see cref="DocumentException"/> (a <see cref="DocumentLimitException"/> included). HTML has no
/// invalid input either. Anything else is a crash, and AFL++ reports hangs.
/// </summary>
internal static class Targets
{
    private static readonly Dictionary<string, Action<Stream>> All = new(StringComparer.Ordinal)
    {
        ["markdown"] = Markdown,
        ["package"] = Package,
        ["docx"] = DocxFile,
        ["html"] = Html,
        ["layout"] = LayoutTarget,
        ["pagemedia"] = PageMedia,
        ["mailmerge"] = MailMergeTarget,
        ["csslayout"] = CssLayout,
        ["mime"] = Mime,
        ["mhtml"] = Mhtml,
        ["opf"] = Opf,
        ["nav"] = Nav,
        ["epub"] = Epub,
    };

    public static IEnumerable<string> Names => All.Keys;

    public static Action<Stream> Get(string name)
    {
        return All.TryGetValue(name, out Action<Stream>? target) ? target : throw new ArgumentException($"Unknown target {name}.", nameof(name));
    }

    /// <summary>
    /// Any bytes read as Markdown with GitHub's extensions and with CommonMark only; the model must answer GetText and the
    /// list labels and resolve every run's formatting; the writer must write it, and its Markdown must read again.
    /// </summary>
    private static void Markdown(Stream input)
    {
        using StreamReader reader = new(input);
        string text = reader.ReadToEnd();
        foreach (MarkdownExtensions extensions in (MarkdownExtensions[])[MarkdownExtensions.GitHub, MarkdownExtensions.None])
        {
            Document document = MarkdownFormat.Parse(text, new MarkdownLoadOptions { Extensions = extensions });
            _ = document.GetText();
            foreach (Paragraph paragraph in document.Descendants<Paragraph>())
            {
                _ = paragraph.ListLabel;
                _ = paragraph.GetEffectiveFormat();
            }

            foreach (Run run in document.Descendants<Run>())
            {
                _ = run.GetEffectiveFormat();
            }

            // The writer must take anything the reader made, and what it writes must read again.
            string written = MarkdownFormat.ToMarkdown(document);
            _ = MarkdownFormat.Parse(written, new MarkdownLoadOptions { Extensions = extensions }).GetText();
        }
    }

    /// <summary>
    /// Any bytes parsed as an HTML document (encoding sniffed); every element must answer Egami.Css's element members, as
    /// selector matching asks them.
    /// </summary>
    private static void Html(Stream input)
    {
        using MemoryStream bytes = new();
        input.CopyTo(bytes);
        Egap.Documents.Html.HtmlDocument document = Egap.Documents.Html.HtmlParsing.Parse(bytes.ToArray(), null);
        Visit(document);

        // The reader maps every page onto the model through the cascade, and the model answers its formatting.
        Document model = HtmlFormat.Load(bytes.ToArray(), out _);
        _ = model.GetText();
        foreach (Paragraph paragraph in model.Descendants<Paragraph>())
        {
            _ = paragraph.ListLabel;
            _ = paragraph.GetEffectiveFormat();
        }

        static void Visit(Egap.Documents.Html.HtmlNode root)
        {
            foreach (Egap.Documents.Html.HtmlElement element in root.Descendants().OfType<Egap.Documents.Html.HtmlElement>())
            {
                Egami.Css.ICssElement css = element;
                _ = css.Parent;
                _ = css.PreviousSibling;
                _ = css.NextSibling;
                _ = css.FirstChild;
                _ = css.IsEmpty;
                _ = css.Id;
                _ = css.HasClass("a");
                _ = css.Attributes.Count();
                if (element.Content is { } content)
                {
                    Visit(content);
                }
            }
        }
    }

    /// <summary>
    /// Any bytes laid out and drawn: as DOCX when they are a package, else as HTML, then each page rendered small. The layout
    /// may throw nothing at all, whatever the reader made (Build Plan task 28).
    /// </summary>
    private static void LayoutTarget(Stream input)
    {
        using MemoryStream bytes = new();
        input.CopyTo(bytes);
        byte[] data = bytes.ToArray();
        Document document;
        try
        {
            bool package = data.Length > 2 && data[0] == (byte)'P' && data[1] == (byte)'K';
            bool wordXml = !package && data.AsSpan().IndexOf("w:document"u8) >= 0;
            document = package || wordXml
                ? Egap.Documents.DocxFormat.Load(package ? new MemoryStream(data) : Wrap(data), new Egap.Documents.DocxLoadOptions { Limits = new DocumentLimits { MaxPartSize = 64 * 1024 * 1024, MaxTotalSize = 256 * 1024 * 1024 } })
                : HtmlFormat.Load(data, out _);
        }
        catch (DocumentException)
        {
            return;
        }

        Egap.Documents.Layout.DocumentLayout layout = document.Layout(new Egap.Documents.Layout.LayoutOptions { Fonts = new Egap.Documents.Layout.FontSettings { UseSystemFonts = false } });
        foreach (Egap.Documents.Layout.PageLayout page in layout.Pages.Take(3))
        {
            _ = page.Render(6);
            if (page.BodyLines.FirstOrDefault() is { } line)
            {
                _ = layout.HitTest(page, line.Bounds.X + 5, line.Bounds.Y + 1);
            }
        }
    }

    /// <summary>
    /// Any bytes read as HTML with its @page margin boxes, running elements and table of contents placeholder, laid out
    /// (two passes for the page numbers), and written back as HTML, which must read again, and as DOCX.
    /// </summary>
    private static void PageMedia(Stream input)
    {
        using MemoryStream bytes = new();
        input.CopyTo(bytes);
        Document document = HtmlFormat.Load(bytes.ToArray(), out _);
        foreach (Field toc in document.GetFields().Where(f => f.Kind == FieldKind.Toc).Take(4).ToList())
        {
            _ = toc.Update();
        }

        Egap.Documents.Layout.DocumentLayout layout = document.Layout(new Egap.Documents.Layout.LayoutOptions { Fonts = new Egap.Documents.Layout.FontSettings { UseSystemFonts = false } });
        foreach (Egap.Documents.Layout.PageLayout page in layout.Pages.Take(2))
        {
            _ = page.Render(6);
        }

        _ = HtmlFormat.Parse(HtmlFormat.ToHtml(document, out _)).GetText();
        DocxFormat.Save(document, Stream.Null);
    }

    /// <summary>
    /// Any bytes read as HTML with flex and grid containers, boxes with rounded corners and shadows, and positioned boxes:
    /// laid out with every page drawn, written back as HTML, which must read and lay out again, and as DOCX, whose
    /// borderless tables must read again.
    /// </summary>
    private static void CssLayout(Stream input)
    {
        using MemoryStream bytes = new();
        input.CopyTo(bytes);
        Document document = HtmlFormat.Load(bytes.ToArray(), out _);
        Egap.Documents.Layout.LayoutOptions options = new() { Fonts = new Egap.Documents.Layout.FontSettings { UseSystemFonts = false } };
        Egap.Documents.Layout.DocumentLayout layout = document.Layout(options);
        foreach (Egap.Documents.Layout.PageLayout page in layout.Pages.Take(3))
        {
            _ = page.Render(6);
        }

        Document again = HtmlFormat.Parse(HtmlFormat.ToHtml(document, out _));
        _ = again.Layout(options).Pages.Count;
        using MemoryStream docx = new();
        DocxFormat.Save(document, docx);
        _ = DocxFormat.Load(new MemoryStream(docx.ToArray())).GetText();
    }

    /// <summary>
    /// Any bytes read as a DOCX template (or a bare document.xml) and merged with records that have nested regions: in
    /// place, one per record and all together; the merged document must lay out and save.
    /// </summary>
    private static void MailMergeTarget(Stream input)
    {
        using MemoryStream bytes = new();
        input.CopyTo(bytes);
        byte[] data = bytes.ToArray();
        Document template;
        try
        {
            bool package = data.Length > 2 && data[0] == (byte)'P' && data[1] == (byte)'K';
            template = DocxFormat.Load(package ? new MemoryStream(data) : Wrap(data), new DocxLoadOptions { Limits = new DocumentLimits { MaxPartSize = 64 * 1024 * 1024, MaxTotalSize = 256 * 1024 * 1024 } });
        }
        catch (DocumentException)
        {
            return;
        }

        static IMergeData Record(int n) => MergeData.From(new Dictionary<string, object?>
        {
            ["Name"] = "Name " + n,
            ["Amount"] = 1234.5 * n,
            ["Date"] = new DateOnly(2026, 10, n + 1),
            ["Empty"] = null,
            ["Lines"] = Enumerable.Range(0, 3).Select(i => (IMergeData)MergeData.From(new Dictionary<string, object?>
            {
                ["Item"] = "Item " + i,
                ["Qty"] = i,
                ["Tags"] = new List<Dictionary<string, object?>> { new() { ["Tag"] = "a" }, new() { ["Tag"] = "b" } },
            })).ToList(),
        });

        Document all = template.MailMergeAll([Record(1), Record(2)], new MailMergeOptions { RemoveEmptyParagraphs = true });
        _ = template.MailMerge([Record(3)]).Single().GetText();
        _ = template.FillMergeFields(Record(4));
        _ = template.GetText();
        Egap.Documents.Layout.DocumentLayout layout = all.Layout(new Egap.Documents.Layout.LayoutOptions { Fonts = new Egap.Documents.Layout.FontSettings { UseSystemFonts = false } });
        _ = layout.Pages[0].Render(6);
        DocxFormat.Save(all, Stream.Null);
    }

    /// <summary>Any bytes read as an OPC package: every part's relationships resolved and every XML part loaded.</summary>
    private static void Package(Stream input)
    {
        try
        {
            Egap.Documents.Docx.OpcPackage package = Egap.Documents.Docx.OpcPackage.Read(input, new DocumentLimits { MaxPartSize = 64 * 1024 * 1024, MaxTotalSize = 256 * 1024 * 1024 });
            foreach (Egap.Documents.Docx.OpcPart part in package.Parts)
            {
                _ = part.Relationships;
                if (part.ContentType.EndsWith("xml", StringComparison.Ordinal))
                {
                    try
                    {
                        _ = part.LoadXml();
                    }
                    catch (DocumentException)
                    {
                    }
                }
            }
        }
        catch (DocumentException)
        {
        }
    }

    /// <summary>
    /// Any bytes as a DOCX: a ZIP package read whole, or anything else as the main part's XML in a package with styles and
    /// numbering, so mutations reach the reader's mapping. The model must then answer GetText, effective formats, list
    /// labels, note marks and fields, and write it back both ways (as read and from the model) to a file that reads again.
    /// </summary>
    private static void DocxFile(Stream input)
    {
        using MemoryStream bytes = new();
        input.CopyTo(bytes);
        bytes.Position = 0;
        Stream package = bytes;
        if (bytes.Length < 2 || bytes.GetBuffer()[0] != (byte)'P' || bytes.GetBuffer()[1] != (byte)'K')
        {
            package = Wrap(bytes.ToArray());
        }

        Document document;
        try
        {
            document = Egap.Documents.DocxFormat.Load(package, new Egap.Documents.DocxLoadOptions { Limits = new DocumentLimits { MaxPartSize = 64 * 1024 * 1024, MaxTotalSize = 256 * 1024 * 1024 } });
        }
        catch (DocumentException)
        {
            return;
        }

        _ = document.GetText();
        foreach (Node node in document.Descendants())
        {
            switch (node)
            {
                case Paragraph paragraph:
                    _ = paragraph.ListLabel;
                    _ = paragraph.GetEffectiveFormat();
                    break;
                case Run run:
                    _ = run.GetEffectiveFormat();
                    break;
                case Footnote note:
                    _ = note.Mark;
                    break;
                case Table table:
                    _ = table.GetEffectiveFormat();
                    break;
                case Cell cell:
                    _ = cell.GetEffectiveFormat();
                    break;
            }
        }

        foreach (Field field in document.GetFields())
        {
            _ = field.Code;
            _ = field.Result;
        }

        _ = document.Clone();

        // The writer must write what the reader read, as read and from the model, and both must read again.
        foreach (bool regenerate in (ReadOnlySpan<bool>)[false, true])
        {
            using MemoryStream written = new();
            Egap.Documents.Docx.DocxWriter.Write(document, written, regenerate);
            written.Position = 0;
            _ = Egap.Documents.DocxFormat.Load(written).GetText();
        }
    }

    /// <summary>Any bytes read as a MIME message; may throw only <see cref="DocumentException"/>. Every part answers its headers.</summary>
    private static void Mime(Stream input)
    {
        byte[] data = ReadAll(input);
        try
        {
            Egap.Documents.Html.MimeEntity root = Egap.Documents.Html.MimeReader.Read(data, new DocumentLimits());
            foreach (Egap.Documents.Html.MimeEntity leaf in root.Leaves())
            {
                _ = leaf.MediaType;
                _ = leaf.ContentId;
                _ = leaf.ContentLocation;
                _ = leaf.BodyText();
            }
        }
        catch (DocumentException)
        {
        }

        // The quoted-printable codec takes any bytes, and what it writes decodes to them.
        byte[] decoded = Egap.Documents.Html.QuotedPrintable.Decode(data);
        string encoded = Egap.Documents.Html.QuotedPrintable.Encode(decoded);
        byte[] again = Egap.Documents.Html.QuotedPrintable.Decode(System.Text.Encoding.ASCII.GetBytes(encoded));
        if (!again.AsSpan().SequenceEqual(Lf(decoded)))
        {
            throw new InvalidOperationException("Quoted-printable did not round trip.");
        }
    }

    /// <summary>Bytes with each lone LF as CRLF, as quoted-printable writes line breaks.</summary>
    private static byte[] Lf(byte[] bytes)
    {
        List<byte> result = new(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 10 && (i == 0 || bytes[i - 1] != 13))
            {
                result.Add(13);
            }

            result.Add(bytes[i]);
        }

        return [.. result];
    }

    /// <summary>Any bytes read as an MHTML archive; may throw only <see cref="DocumentException"/>. The model is written back and read again.</summary>
    private static void Mhtml(Stream input)
    {
        Document document;
        try
        {
            document = HtmlFormat.LoadMhtml(ReadAll(input), out _);
        }
        catch (DocumentException)
        {
            return;
        }

        _ = document.GetText();
        string written = document.ToMhtml();
        _ = HtmlFormat.LoadMhtml(System.Text.Encoding.ASCII.GetBytes(written), out _).GetText();
    }

    /// <summary>Any bytes read as a book's package document, in a container that holds nothing else; may throw only <see cref="DocumentException"/>.</summary>
    private static void Opf(Stream input)
    {
        byte[] book = Book(("OEBPS/content.opf", ReadAll(input)));
        ReadBook(book);
    }

    /// <summary>Any bytes read as a book's EPUB 3 navigation document and as its EPUB 2 NCX; may throw only <see cref="DocumentException"/>.</summary>
    private static void Nav(Stream input)
    {
        byte[] data = ReadAll(input);
        byte[] opf = System.Text.Encoding.UTF8.GetBytes(
            "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"u\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
            + "<dc:identifier id=\"u\">x</dc:identifier><dc:title>t</dc:title><dc:language>en</dc:language></metadata><manifest>"
            + "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/><item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>"
            + "<item id=\"c\" href=\"c.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine toc=\"ncx\"><itemref idref=\"c\"/></spine></package>");
        byte[] chapter = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1 id=\"a\">A</h1><p id=\"b\">B</p></body></html>"u8.ToArray();
        ReadBook(Book(("OEBPS/content.opf", opf), ("OEBPS/nav.xhtml", data), ("OEBPS/c.xhtml", chapter)));
        ReadBook(Book(("OEBPS/content.opf", opf), ("OEBPS/toc.ncx", data), ("OEBPS/c.xhtml", chapter)));
    }

    /// <summary>
    /// Any bytes read as an EPUB book; may throw only <see cref="DocumentException"/>. The model is written as EPUB, which
    /// must read again.
    /// </summary>
    private static void Epub(Stream input)
    {
        ReadBook(ReadAll(input));
    }

    private static void ReadBook(byte[] book)
    {
        Document document;
        try
        {
            document = EpubFormat.Load(new MemoryStream(book), new EpubLoadOptions { Limits = new DocumentLimits { MaxPartSize = 64 * 1024 * 1024, MaxTotalSize = 256 * 1024 * 1024 } });
        }
        catch (DocumentException)
        {
            return;
        }

        _ = document.GetText();
        foreach (Paragraph paragraph in document.Descendants<Paragraph>())
        {
            _ = paragraph.GetEffectiveFormat();
        }

        _ = EpubFormat.Load(new MemoryStream(document.ToEpub())).GetText();
    }

    /// <summary>A ZIP container with an EPUB mimetype, a container.xml pointing at OEBPS/content.opf, and the files.</summary>
    private static byte[] Book(params (string Name, byte[] Data)[] files)
    {
        using MemoryStream stream = new();
        using (System.IO.Compression.ZipArchive zip = new(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            Add("mimetype", "application/epub+zip"u8.ToArray());
            Add("META-INF/container.xml", "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>"u8.ToArray());
            foreach ((string name, byte[] data) in files)
            {
                Add(name, data);
            }

            void Add(string name, byte[] data)
            {
                using Stream entry = zip.CreateEntry(name).Open();
                entry.Write(data);
            }
        }

        return stream.ToArray();
    }

    private static byte[] ReadAll(Stream input)
    {
        using MemoryStream bytes = new();
        input.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static MemoryStream Wrap(byte[] documentXml)
    {
        const string w = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
        Egap.Documents.Docx.OpcPackageWriter writer = new();
        writer.AddPart("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", documentXml);
        writer.AddPart("/word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml", System.Text.Encoding.UTF8.GetBytes(
            "<w:styles " + w + "><w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>"
            + "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr></w:style>"
            + "<w:style w:type=\"character\" w:styleId=\"Strong\"><w:name w:val=\"Strong\"/><w:rPr><w:b/></w:rPr></w:style>"
            + "<w:style w:type=\"table\" w:styleId=\"Grid\"><w:name w:val=\"Table Grid\"/><w:tblStylePr w:type=\"firstRow\"><w:rPr><w:b/></w:rPr></w:tblStylePr></w:style></w:styles>"));
        writer.AddPart("/word/numbering.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml", System.Text.Encoding.UTF8.GetBytes(
            "<w:numbering " + w + "><w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/></w:lvl>"
            + "<w:lvl w:ilvl=\"1\"><w:numFmt w:val=\"lowerRoman\"/><w:lvlText w:val=\"%1.%2\"/></w:lvl></w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>"));
        writer.AddRelationship("/", "rId1", Egap.Documents.Docx.OpcRelationshipTypes.OfficeDocument, "word/document.xml");
        writer.AddRelationship("/word/document.xml", "rId1", Egap.Documents.Docx.OpcRelationshipTypes.Styles, "styles.xml");
        writer.AddRelationship("/word/document.xml", "rId2", Egap.Documents.Docx.OpcRelationshipTypes.Numbering, "numbering.xml");
        MemoryStream stream = new();
        writer.Save(stream);
        stream.Position = 0;
        return stream;
    }
}
