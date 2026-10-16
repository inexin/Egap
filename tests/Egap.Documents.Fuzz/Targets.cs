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
            Docx.OpcPackage package = Docx.OpcPackage.Read(input, new DocumentLimits { MaxPartSize = 64 * 1024 * 1024, MaxTotalSize = 256 * 1024 * 1024 });
            foreach (Docx.OpcPart part in package.Parts)
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
}
