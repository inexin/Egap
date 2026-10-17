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
    /// Any bytes parsed as an HTML document (encoding sniffed) and as fragments in a few contexts; every element must answer
    /// Egami.Css's element members, as selector matching asks them.
    /// </summary>
    private static void Html(Stream input)
    {
        using MemoryStream bytes = new();
        input.CopyTo(bytes);
        Egap.Documents.Html.HtmlDocument document = Egap.Documents.Html.HtmlTreeBuilder.Parse(bytes.ToArray(), out _);
        Visit(document);

        // The reader maps every page onto the model through the cascade, and the model answers its formatting.
        Document model = HtmlFormat.Load(bytes.ToArray(), out _);
        _ = model.GetText();
        foreach (Paragraph paragraph in model.Descendants<Paragraph>())
        {
            _ = paragraph.ListLabel;
            _ = paragraph.GetEffectiveFormat();
        }
        string text = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
        foreach (string context in (string[])["body", "td", "template", "select", "svg"])
        {
            Visit(Egap.Documents.Html.HtmlTreeBuilder.ParseFragment(text, context, context == "svg" ? Egap.Documents.Html.HtmlElement.SvgNamespace : Egap.Documents.Html.HtmlElement.HtmlNamespace));
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
