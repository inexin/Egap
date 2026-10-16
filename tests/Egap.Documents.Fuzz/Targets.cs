namespace Egap.Documents.Fuzz;

/// <summary>
/// The fuzz targets for Egap.Documents' readers. A reader must never throw on any input (Markdown has no invalid input);
/// anything thrown is a crash, and AFL++ reports hangs.
/// </summary>
internal static class Targets
{
    private static readonly Dictionary<string, Action<Stream>> All = new(StringComparer.Ordinal)
    {
        ["markdown"] = Markdown,
    };

    public static IEnumerable<string> Names => All.Keys;

    public static Action<Stream> Get(string name)
    {
        return All.TryGetValue(name, out Action<Stream>? target) ? target : throw new ArgumentException($"Unknown target {name}.", nameof(name));
    }

    /// <summary>
    /// Any bytes read as Markdown with GitHub's extensions and with CommonMark only; the model must answer GetText and the
    /// list labels and resolve every run's formatting.
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
        }
    }
}
