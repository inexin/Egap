using System.CommandLine;
using System.Globalization;
using System.Text.RegularExpressions;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary>
/// <c>egap redact</c> and the <c>redact</c> step: text by phrase or regular expression and areas by rectangle, removed for
/// real (glyphs, image pixels and paths under them), and the file written whole so no earlier revision keeps them.
/// </summary>
internal sealed class RedactEdit : Edit
{
    private readonly Option<string[]> _text = new("--text") { Description = "A phrase to remove; give it more than once.", HelpName = "phrase", AllowMultipleArgumentsPerToken = false };
    private readonly Option<string[]> _regex = new("--regex") { Description = "A .NET regular expression to remove, such as \\d{3}-\\d{2}-\\d{4}; give it more than once.", HelpName = "pattern", AllowMultipleArgumentsPerToken = false };
    private readonly Option<string[]> _rect = new("--rect")
    {
        Description = "An area to remove, page:left,bottom,width,height in lengths from the page's lower left corner, such as 1:20mm,250mm,80mm,10mm; give it more than once.",
        HelpName = "page:x,y,w,h",
        AllowMultipleArgumentsPerToken = false,
    };

    private readonly Option<bool> _matchCase = new("--match-case") { Description = "Match --text with the case as written." };
    private readonly Option<bool> _wholeWord = new("--whole-word") { Description = "Match --text only as whole words." };
    private readonly PageOption _pages = new("The pages to search");
    private readonly Option<bool> _markOnly = new("--mark-only") { Description = "Only mark the areas with redaction annotations, for review; remove nothing yet." };
    private readonly Option<string?> _images = new("--images") { Description = "Image parts under an area: pixels (blacked out, the default), remove (the whole image) or none.", HelpName = "how" };
    private readonly Option<string?> _paths = new("--paths") { Description = "Vector paths: covered (removed when fully covered, the default), touched (removed when touched) or none.", HelpName = "how" };

    /// <inheritdoc/>
    public override string Name => "redact";

    /// <inheritdoc/>
    public override string Description => "Remove text by phrase or pattern, and areas by rectangle, for real; the file is written whole.";

    /// <inheritdoc/>
    public override void AddTo(Command command)
    {
        foreach (Option option in (Option[])[_text, _regex, _rect, _matchCase, _wholeWord, _pages.Pages, _markOnly, _images, _paths])
        {
            command.Options.Add(option);
        }
    }

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        string[] phrases = result.GetValue(_text) ?? [];
        Regex[] patterns = [.. (result.GetValue(_regex) ?? []).Select(Pattern)];
        List<(int Page, PdfRectangle Box)> areas = [.. (result.GetValue(_rect) ?? []).Select(Area)];
        if (phrases.Length + patterns.Length + areas.Count == 0)
        {
            throw new UsageException("Nothing to redact: give --text, --regex or --rect.");
        }

        PdfFindOptions find = new() { MatchCase = result.GetValue(_matchCase), WholeWord = result.GetValue(_wholeWord), Pages = _pages.Range(result) };
        PdfRedactionOptions options = PdfRedactionOptions.Default with
        {
            Images = result.GetValue(_images) switch
            {
                null or "pixels" => PdfRedactImages.Pixels,
                "remove" => PdfRedactImages.Remove,
                "none" => PdfRedactImages.None,
                string other => throw new UsageException($"--images '{other}' is not pixels, remove or none."),
            },
            Paths = result.GetValue(_paths) switch
            {
                null or "covered" => PdfRedactPaths.Covered,
                "touched" => PdfRedactPaths.Touched,
                "none" => PdfRedactPaths.None,
                string other => throw new UsageException($"--paths '{other}' is not covered, touched or none."),
            },
        };
        bool markOnly = result.GetValue(_markOnly);
        return (document, context) =>
        {
            int marked = 0;
            foreach (string phrase in phrases)
            {
                marked += document.MarkRedactions(phrase, find).Count;
            }

            foreach (Regex pattern in patterns)
            {
                marked += document.MarkRedactions(pattern, find).Count;
            }

            foreach ((int page, PdfRectangle box) in areas)
            {
                if (page > document.Pages.Count)
                {
                    throw new InvalidOperationException($"--rect is on page {page}, but the file has {document.Pages.Count} pages.");
                }

                document.Pages[page - 1].Annotations.Add(new PdfRedactAnnotation(box));
                marked++;
            }

            if (markOnly)
            {
                session.Info($"{context.File.Display}: marked {marked} areas");
                return document;
            }

            PdfRedactionResult removed = document.ApplyRedactions(options);
            context.Rewrite = true;
            session.Info($"{context.File.Display}: {marked} areas, {removed.Glyphs} glyphs, {removed.Images} images and {removed.Paths} paths removed");
            return document;
        };
    }

    private static Regex Pattern(string text)
    {
        try
        {
            return new Regex(text, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));
        }
        catch (ArgumentException error)
        {
            throw new UsageException($"--regex '{text}': {error.Message}");
        }
    }

    private static (int Page, PdfRectangle Box) Area(string text)
    {
        int colon = text.IndexOf(':', StringComparison.Ordinal);
        string[] parts = colon > 0 ? text[(colon + 1)..].Split(',', StringSplitOptions.TrimEntries) : [];
        if (colon <= 0 || parts.Length != 4 || !int.TryParse(text[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out int page) || page < 1)
        {
            throw new UsageException($"--rect '{text}' is not page:left,bottom,width,height, such as 1:20mm,250mm,80mm,10mm.");
        }

        double[] numbers = [.. parts.Select(part => Values.Points(part, "--rect"))];
        return numbers[2] > 0 && numbers[3] > 0
            ? (page, new PdfRectangle(numbers[0], numbers[1], numbers[0] + numbers[2], numbers[1] + numbers[3]))
            : throw new UsageException($"--rect '{text}': the width and height must be above zero.");
    }
}

/// <summary>
/// <c>egap sanitize</c> and the <c>sanitize</c> step: JavaScript and actions, attachments, metadata, hidden layers, comments,
/// form data, thumbnails and private data removed (all of them unless <c>--keep</c> names some), the file written whole.
/// </summary>
internal sealed class SanitizeEdit : Edit
{
    private static readonly string[] Kinds = ["metadata", "javascript", "attachments", "hidden-layers", "comments", "form-data", "thumbnails", "private-data"];

    private readonly Option<string?> _keep = new("--keep") { Description = $"What to leave in, a list of {string.Join(", ", Kinds)}.", HelpName = "kinds" };

    /// <inheritdoc/>
    public override string Name => "sanitize";

    /// <inheritdoc/>
    public override string Description => "Strip hidden data: JavaScript and actions, attachments, metadata, hidden layers, comments, form data, thumbnails and private data.";

    /// <inheritdoc/>
    public override void AddTo(Command command) => command.Options.Add(_keep);

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        HashSet<string> keep = new(StringComparer.Ordinal);
        foreach (string kind in (result.GetValue(_keep) ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            _ = Kinds.Contains(kind) ? keep.Add(kind) : throw new UsageException($"--keep: '{kind}' is not one of {string.Join(", ", Kinds)}.");
        }

        PdfSanitizeOptions options = new()
        {
            Metadata = !keep.Contains("metadata"),
            JavaScript = !keep.Contains("javascript"),
            Attachments = !keep.Contains("attachments"),
            HiddenLayers = !keep.Contains("hidden-layers"),
            Comments = !keep.Contains("comments"),
            FormData = !keep.Contains("form-data"),
            Thumbnails = !keep.Contains("thumbnails"),
            PrivateData = !keep.Contains("private-data"),
        };
        return (document, context) =>
        {
            PdfSanitizeResult removed = document.Sanitize(options);
            context.Rewrite = true;
            context.KeepModificationDate = options.Metadata;
            session.Info($"{context.File.Display}: removed {removed.Actions} actions, {removed.Attachments} attachments, {removed.Metadata} metadata, {removed.Layers} layers, {removed.Comments} comments, {removed.Thumbnails} thumbnails; reset {removed.Fields} fields");
            return document;
        };
    }
}
