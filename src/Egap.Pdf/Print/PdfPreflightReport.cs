using System.Globalization;

namespace Egap.Pdf;

/// <summary>What a <see cref="PdfPreflightItem"/> reports.</summary>
public enum PdfPreflightItemKind
{
    /// <summary>An output intent of the document: its condition and its profile.</summary>
    OutputIntent,

    /// <summary>A colour space content paints with, and how often.</summary>
    ColorSpace,

    /// <summary>An image, with its colour space and effective resolution where it is placed largest.</summary>
    Image,

    /// <summary>A font that draws text, and whether it is embedded.</summary>
    Font,

    /// <summary>Transparency: a group, a soft mask, an alpha under 1 or a blend mode other than Normal.</summary>
    Transparency,

    /// <summary>Content painted with overprint on.</summary>
    Overprint,

    /// <summary>A stroke thinner than the profile's thinnest rule (0.25 pt when the profile sets none).</summary>
    Hairline,

    /// <summary>A page's trim and bleed boxes and the bleed between them.</summary>
    PageBoxes,

    /// <summary>A page's total ink: the most anywhere, and the share over the profile's limit.</summary>
    InkCoverage,
}

/// <summary>
/// One thing a preflight found in a document: what it is, the page it is on (null for the whole document), a line describing
/// it, and the details that apply to its kind. Uses of the same thing on a page are counted in one item.
/// </summary>
/// <param name="Kind">What the item reports.</param>
/// <param name="Page">The page number (from 1), or null for the document.</param>
/// <param name="Description">A line saying what was found.</param>
public sealed record PdfPreflightItem(PdfPreflightItemKind Kind, int? Page, string Description)
{
    /// <summary>
    /// Gets the item's name: an image's or colour space's resource name, a font's PostScript name, an output intent's condition
    /// identifier; null when it has none.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>Gets the kind of colour space, for colour spaces, images and output intents.</summary>
    public PdfColorSpaceKinds ColorSpace { get; init; }

    /// <summary>
    /// Gets the colour space as the file names it: <c>DeviceCMYK</c>, <c>ICCBased RGB</c>, <c>Separation</c>,
    /// <c>Indexed</c> and so on; null when it does not apply.
    /// </summary>
    public string? ColorSpaceName { get; init; }

    /// <summary>Gets the description in the ICC profile behind the colour space or output intent, such as <c>Coated FOGRA39</c>.</summary>
    public string? ProfileName { get; init; }

    /// <summary>Gets the spot inks a Separation or DeviceN space names, without All and None.</summary>
    public IReadOnlyList<string> Spots { get; init; } = [];

    /// <summary>Gets an image's effective resolution in pixels per inch where it is placed largest.</summary>
    public double? Dpi { get; init; }

    /// <summary>Gets an image's bits per colour component.</summary>
    public int? BitsPerComponent { get; init; }

    /// <summary>Gets whether a font's program is in the file.</summary>
    public bool? Embedded { get; init; }

    /// <summary>Gets a hairline's width in points at the page's scale.</summary>
    public double? LineWidth { get; init; }

    /// <summary>Gets a page's bleed past its trim box in millimetres, the smallest of its four sides; null without a trim box.</summary>
    public double? Bleed { get; init; }

    /// <summary>Gets a page's most total ink anywhere, in percent.</summary>
    public double? MaxInkCoverage { get; init; }

    /// <summary>Gets the share of a page (0 to 1) whose total ink is over the profile's limit.</summary>
    public double? ShareOverLimit { get; init; }

    /// <summary>Gets how often the page uses the item: the number of paints in a colour space, placements of an image and so on.</summary>
    public int Count { get; init; } = 1;

    /// <summary>The item as a line: its page and description.</summary>
    public override string ToString()
    {
        return Page is int page ? string.Create(CultureInfo.InvariantCulture, $"page {page}: {Description}") : Description;
    }
}

/// <summary>
/// What <see cref="PdfPreflight.Run(PdfDocument, PdfPrintProfile?, CancellationToken)"/> found: every colour space, profile, spot ink, output
/// intent, image resolution, font, transparency, overprint and hairline the document uses, page by page, and the issues
/// that break the print profile it was checked against. Issues are <see cref="PdfAIssue"/>s, as for PDF/A; preflight fixes
/// nothing, so none is fixable.
/// </summary>
public sealed class PdfPreflightReport
{
    internal PdfPreflightReport(PdfPrintProfile profile, IReadOnlyList<PdfPreflightItem> items, IReadOnlyList<PdfAIssue> issues)
    {
        Profile = profile;
        Items = items;
        Issues = issues;
    }

    /// <summary>The print profile the document was checked against.</summary>
    public PdfPrintProfile Profile { get; }

    /// <summary>Everything found: the document's output intents first, then each page's items in page order.</summary>
    public IReadOnlyList<PdfPreflightItem> Items { get; }

    /// <summary>What breaks the print profile.</summary>
    public IReadOnlyList<PdfAIssue> Issues { get; }

    /// <summary>Whether nothing breaks the print profile.</summary>
    public bool Passed => Issues.Count == 0;

    /// <summary>Every kind of colour space the document paints with, images and shadings included.</summary>
    public PdfColorSpaceKinds ColorSpaces => Items.Where(i => i.Kind is PdfPreflightItemKind.ColorSpace or PdfPreflightItemKind.Image)
        .Aggregate(PdfColorSpaceKinds.None, (kinds, item) => kinds | item.ColorSpace);

    /// <summary>Every spot ink the document paints with, in name order.</summary>
    public IReadOnlyList<string> Spots => [.. Items.SelectMany(i => i.Spots).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The document's output intents.</summary>
    public IEnumerable<PdfPreflightItem> OutputIntents => Items.Where(i => i.Kind == PdfPreflightItemKind.OutputIntent);

    /// <summary>The items on one page (from 1).</summary>
    public IEnumerable<PdfPreflightItem> ItemsOn(int page) => Items.Where(i => i.Page == page);

    /// <summary>A heading, a line per item and a line per issue.</summary>
    public override string ToString()
    {
        List<string> lines = [$"Preflight against {Profile.Name}: {(Passed ? "passed" : $"{Issues.Count} issue{(Issues.Count == 1 ? "" : "s")}")}"];
        lines.AddRange(Items.Select(i => "  " + i));
        lines.AddRange(Issues.Select(i => $"  issue {i.Clause}{(i.Page is int p ? $" page {p}" : "")}: {i.Description}"));
        return string.Join(Environment.NewLine, lines);
    }
}
