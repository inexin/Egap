namespace Egap.Documents.Fuzz;

/// <summary>
/// The fuzz targets for Egap.Documents' readers. Markdown has no invalid input, so its reader must never throw; the DOCX
/// readers may throw only <see cref="DocumentException"/> (a <see cref="DocumentLimitException"/> included). Anything else
/// is a crash, and AFL++ reports hangs.
/// </summary>
internal static class Targets
{
    private static readonly Dictionary<string, Action<Stream>> All = new(StringComparer.Ordinal)
    {
        ["markdown"] = Markdown,
        ["package"] = Package,
        ["docx"] = DocxFile,
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
    /// labels, note marks and fields.
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
            document = Egap.Documents.Docx.DocxFormat.Load(package, new Egap.Documents.Docx.DocxLoadOptions { Limits = new DocumentLimits { MaxPartSize = 64 * 1024 * 1024, MaxTotalSize = 256 * 1024 * 1024 } });
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
