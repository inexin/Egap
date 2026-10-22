using System.CommandLine;

using Egap.Pdf;

namespace Egap.Cli.Host;

/// <summary>
/// <c>--pages</c> in the one syntax egap uses everywhere, the one <see cref="PdfPageRange"/> reads (CLI design Q5):
/// <c>1,3,5-10</c>, <c>z</c> for the last page, <c>r2</c> for the second to last, <c>10-1</c> backwards, <c>:odd</c> and
/// <c>:even</c>, <c>x</c> to leave pages out, and <c>all</c>.
/// </summary>
internal sealed class PageOption
{
    public PageOption(string what = "The pages to use")
    {
        Pages = new("--pages", "-p") { Description = $"{what}, such as 1-3,z or 2-z:even (z is the last page, r2 the second to last, x leaves pages out); all by default.", HelpName = "range" };
    }

    public Option<string?> Pages { get; }

    /// <summary>The range given, or <see langword="null"/> for every page; throws <see cref="UsageException"/> for a wrong one.</summary>
    public PdfPageRange? Range(ParseResult result) => Parse(result.GetValue(Pages), Pages.Name);

    /// <summary>Reads a page range; <see langword="null"/> text means every page.</summary>
    public static PdfPageRange? Parse(string? text, string option)
    {
        if (text is null)
        {
            return null;
        }

        return PdfPageRange.TryParse(text, out PdfPageRange? range) ? range
            : throw new UsageException($"{option} '{text}' is not a page range, such as 1-3,z, 2-z:even or 1-z,x5.");
    }

    /// <summary>The 0-based indices of the pages <paramref name="range"/> chooses in a document; every page when it is null.</summary>
    public static IReadOnlyList<int> Indices(PdfDocument document, PdfPageRange? range)
    {
        try
        {
            return (range ?? PdfPageRange.All).Resolve(document.Pages.Count);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException($"The pages '{range!.Text}' are not all in the file, which has {document.Pages.Count} page{(document.Pages.Count == 1 ? string.Empty : "s")}.");
        }
    }
}
