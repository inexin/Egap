using System.Globalization;

namespace Egap.Pdf;

/// <summary>
/// Preflight for print: lists what a document uses and checks it against a <see cref="PdfPrintProfile"/>. The report shows
/// every output intent, colour space (with its ICC profile and spot names), image resolution, font, transparency, overprint
/// and hairline, page by page, so a file can be shown to hold CMYK under a FOGRA39 intent, or found to carry an RGB photo.
/// Preflight changes nothing.
/// </summary>
/// <example>
/// <code>
/// using PdfDocument pdf = PdfDocument.Open("print.pdf");
/// PdfPreflightReport report = PdfPreflight.Run(pdf, PdfPrintProfile.SheetfedCoated);
/// foreach (PdfPreflightItem item in report.Items)
/// {
///     Console.WriteLine(item);
/// }
///
/// foreach (PdfAIssue issue in report.Issues)
/// {
///     Console.WriteLine($"{issue.Clause}: {issue.Description} (page {issue.Page})");
/// }
/// </code>
/// </example>
public static class PdfPreflight
{
    /// <summary>The thinnest stroke that is not reported as a hairline when the profile sets no limit, in points.</summary>
    public const double DefaultHairline = 0.25;

    /// <summary>
    /// Lists what the document uses and checks it against <paramref name="profile"/>; with no profile, only the list
    /// (and the checks of a profile that allows everything, so no issues).
    /// </summary>
    /// <param name="document">The document to check.</param>
    /// <param name="profile">The print job's requirements, or null for none.</param>
    /// <param name="cancellationToken">Stops the run between pages.</param>
    public static PdfPreflightReport Run(PdfDocument document, PdfPrintProfile? profile = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPrintProfile used = profile ?? new PdfPrintProfile { Name = "no profile", RequireEmbeddedFonts = false };
        List<PdfPreflightItem> items = new Print.Inventory(document, used.MinLineWidth ?? DefaultHairline).Run(cancellationToken);
        if (used.MaxInkCoverage is double limit)
        {
            items = WithInk(document, items, limit, used.InkCoverageDpi, cancellationToken);
        }

        return new PdfPreflightReport(used, items, Check(items, used));
    }

    /// <summary>The items with each page's ink coverage after its own, measured on its plates (report only, design Q8).</summary>
    private static List<PdfPreflightItem> WithInk(PdfDocument document, List<PdfPreflightItem> items, double limit, double dpi, CancellationToken cancellationToken)
    {
        List<PdfPreflightItem> result = [.. items.Where(i => i.Page is null)];
        PdfRenderOptions render = new() { Dpi = dpi, Intent = PdfRenderIntent.Print, Annotations = PdfAnnotationRendering.All };
        for (int i = 0; i < document.Pages.Count; i++)
        {
            result.AddRange(items.Where(item => item.Page == i + 1));
            double max, share;
            try
            {
                using PdfSeparations plates = document.Pages[i].RenderSeparations(render, cancellationToken: cancellationToken);
                (max, share) = (plates.MaxCoverage, plates.ShareOver(limit));
            }
            catch (PdfLimitException e)
            {
                result.Add(new PdfPreflightItem(PdfPreflightItemKind.InkCoverage, i + 1, $"total ink not measured: {e.Message}"));
                continue;
            }

            result.Add(new PdfPreflightItem(PdfPreflightItemKind.InkCoverage, i + 1, string.Create(CultureInfo.InvariantCulture,
                $"total ink up to {max:0.#}%, {share:P1} of the page over {limit:0}%"))
            {
                MaxInkCoverage = max,
                ShareOverLimit = share,
            });
        }

        return result;
    }

    /// <summary>The issues each item raises under the profile.</summary>
    internal static List<PdfAIssue> Check(IReadOnlyList<PdfPreflightItem> items, PdfPrintProfile profile)
    {
        List<PdfAIssue> issues = [];
        void Issue(string clause, string description, int? page) => issues.Add(new PdfAIssue(clause, description, page, false, false));
        foreach (PdfPreflightItem item in items)
        {
            switch (item.Kind)
            {
                case PdfPreflightItemKind.ColorSpace when (item.ColorSpace & ~profile.AllowedColorSpaces) != 0:
                    Issue("ColorSpace", $"{item.Description}: {profile.Name} does not allow {item.ColorSpaceName}", item.Page);
                    break;
                case PdfPreflightItemKind.Image when profile.MinImageDpi is double min && item.Dpi is double dpi:
                    double needed = item.BitsPerComponent == 1 ? 2 * min : min;
                    if (dpi < needed - 0.5)
                    {
                        Issue("ImageResolution", string.Create(CultureInfo.InvariantCulture,
                            $"{item.Description}: under the {needed:0.#} dpi {profile.Name} needs{(item.BitsPerComponent == 1 ? " for line art" : "")}"), item.Page);
                    }

                    break;
                case PdfPreflightItemKind.Font when profile.RequireEmbeddedFonts && item.Embedded == false:
                    Issue("Font", $"{item.Name} is not embedded", item.Page);
                    break;
                case PdfPreflightItemKind.Hairline when profile.MinLineWidth is double width:
                    Issue("LineWidth", string.Create(CultureInfo.InvariantCulture,
                        $"{item.Description}: thinner than the {width:0.###} pt {profile.Name} needs"), item.Page);
                    break;
                case PdfPreflightItemKind.Transparency when !profile.AllowTransparency:
                    Issue("Transparency", $"{item.Description}: {profile.Name} does not allow transparency", item.Page);
                    break;
                case PdfPreflightItemKind.PageBoxes when profile.RequiredBleed is double bleed:
                    if (item.Bleed is not double found)
                    {
                        Issue("TrimBox", "the page has no trim box, so its bleed cannot be checked", item.Page);
                    }
                    else if (found < bleed - 0.01)
                    {
                        Issue("Bleed", string.Create(CultureInfo.InvariantCulture, $"bleed {found:0.##} mm, under the {bleed:0.##} mm {profile.Name} needs"), item.Page);
                    }

                    break;
                case PdfPreflightItemKind.InkCoverage when profile.MaxInkCoverage is double limit && item.MaxInkCoverage is double max && max > limit + 0.5:
                    Issue("InkCoverage", string.Create(CultureInfo.InvariantCulture,
                        $"total ink up to {max:0}%, over the {limit:0}% {profile.Name} allows on {item.ShareOverLimit ?? 0:P1} of the page"), item.Page);
                    break;
            }
        }

        return issues;
    }
}
