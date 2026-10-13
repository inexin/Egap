namespace Egap.Pdf;

/// <summary>How a destination fits its page in the window (12.3.2.2).</summary>
public enum PdfDestinationFit
{
    /// <summary><c>/XYZ</c>: <see cref="PdfDestination.Left"/> and <see cref="PdfDestination.Top"/> at the window's top left, magnified by <see cref="PdfDestination.Zoom"/>.</summary>
    Xyz,

    /// <summary><c>/Fit</c>: the whole page in the window.</summary>
    Fit,

    /// <summary><c>/FitH</c>: the page's width in the window, <see cref="PdfDestination.Top"/> at its top.</summary>
    FitH,

    /// <summary><c>/FitV</c>: the page's height in the window, <see cref="PdfDestination.Left"/> at its left.</summary>
    FitV,

    /// <summary><c>/FitR</c>: the rectangle from <see cref="PdfDestination.Left"/>, <see cref="PdfDestination.Bottom"/> to <see cref="PdfDestination.Right"/>, <see cref="PdfDestination.Top"/> in the window.</summary>
    FitR,

    /// <summary><c>/FitB</c>: the page's bounding box in the window.</summary>
    FitB,

    /// <summary><c>/FitBH</c>: the bounding box's width in the window, <see cref="PdfDestination.Top"/> at its top.</summary>
    FitBH,

    /// <summary><c>/FitBV</c>: the bounding box's height in the window, <see cref="PdfDestination.Left"/> at its left.</summary>
    FitBV,
}

/// <summary>
/// An explicit destination (12.3.2.2): a page and how to show it. The page is a reference to a page of this document, or
/// a page index (from 0) for a destination in another file (<see cref="PdfGoToRemoteAction"/>). A coordinate or zoom of
/// <see langword="null"/> leaves the viewer's current value unchanged.
/// </summary>
public sealed record PdfDestination
{
    private static readonly string[] FitNames = ["XYZ", "Fit", "FitH", "FitV", "FitR", "FitB", "FitBH", "FitBV"];

    private PdfDestination(PdfReference? pageReference, int? pageIndex, PdfDestinationFit fit)
    {
        PageReference = pageReference;
        PageIndex = pageIndex;
        Fit = fit;
    }

    /// <summary>The page of this document, or <see langword="null"/> when the destination gives a page index.</summary>
    public PdfReference? PageReference { get; }

    /// <summary>The page index, from 0, when the destination gives one rather than a page; <see langword="null"/> otherwise.</summary>
    public int? PageIndex { get; }

    /// <summary>How the page fits the window.</summary>
    public PdfDestinationFit Fit { get; }

    /// <summary>The x at the window's left edge, for <c>/XYZ</c>, <c>/FitV</c>, <c>/FitR</c> and <c>/FitBV</c>.</summary>
    public double? Left { get; init; }

    /// <summary>The y at the window's top edge, for <c>/XYZ</c>, <c>/FitH</c>, <c>/FitR</c> and <c>/FitBH</c>.</summary>
    public double? Top { get; init; }

    /// <summary>The x at the right edge of the rectangle, for <c>/FitR</c>.</summary>
    public double? Right { get; init; }

    /// <summary>The y at the bottom edge of the rectangle, for <c>/FitR</c>.</summary>
    public double? Bottom { get; init; }

    /// <summary>The magnification for <c>/XYZ</c>: 1 is 100%; <see langword="null"/> or 0 keeps the current one.</summary>
    public double? Zoom { get; init; }

    /// <summary>A destination on a page of this document.</summary>
    public static PdfDestination ToPage(PdfPage page, PdfDestinationFit fit = PdfDestinationFit.Fit)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new(page.Reference ?? throw new ArgumentException("The page has no reference of its own.", nameof(page)), null, fit);
    }

    /// <summary>A destination on a page given by its index, from 0, as a destination in another file is.</summary>
    public static PdfDestination ToPageIndex(int pageIndex, PdfDestinationFit fit = PdfDestinationFit.Fit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        return new(null, pageIndex, fit);
    }

    /// <summary>
    /// A destination from its array <c>[page /Fit ...]</c>; <see langword="null"/> when the array names no page or no fit.
    /// Missing or non-numeric parameters read as <see langword="null"/>, as viewers read them.
    /// </summary>
    public static PdfDestination? FromArray(PdfArray? array)
    {
        if (array is not { Count: >= 1 })
        {
            return null;
        }

        PdfReference? reference = array[0] as PdfReference;
        int? index = reference is null && array.GetInt(0) is long number && number is >= 0 and <= int.MaxValue ? (int)number : null;
        if (reference is null && index is null)
        {
            return null;
        }

        // A destination without a fit name is shown whole, as pdf.js shows it.
        int fitIndex = array.Count < 2 ? 1 : Array.IndexOf(FitNames, array.GetName(1)?.Value);
        if (fitIndex < 0)
        {
            return null;
        }

        PdfDestinationFit fit = (PdfDestinationFit)fitIndex;
        PdfDestination destination = new(reference, index, fit);
        double? At(int i)
        {
            return i < array.Count && array.GetNumber(i) is double value && double.IsFinite(value) ? value : null;
        }

        return fit switch
        {
            PdfDestinationFit.Xyz => destination with { Left = At(2), Top = At(3), Zoom = At(4) },
            PdfDestinationFit.FitH or PdfDestinationFit.FitBH => destination with { Top = At(2) },
            PdfDestinationFit.FitV or PdfDestinationFit.FitBV => destination with { Left = At(2) },
            PdfDestinationFit.FitR => destination with { Left = At(2), Bottom = At(3), Right = At(4), Top = At(5) },
            _ => destination,
        };
    }

    /// <summary>The destination as its array, with <c>null</c> for each parameter left unset.</summary>
    public PdfArray ToArray()
    {
        PdfArray array = [PageReference ?? (PdfObject)new PdfInteger(PageIndex ?? 0), PdfName.Get(FitNames[(int)Fit])];
        double?[] parameters = Fit switch
        {
            PdfDestinationFit.Xyz => [Left, Top, Zoom],
            PdfDestinationFit.FitH or PdfDestinationFit.FitBH => [Top],
            PdfDestinationFit.FitV or PdfDestinationFit.FitBV => [Left],
            PdfDestinationFit.FitR => [Left, Bottom, Right, Top],
            _ => [],
        };
        foreach (double? parameter in parameters)
        {
            array.Add(parameter is double value ? Number(value) : PdfNull.Instance);
        }

        return array;
    }

    /// <summary>The index of the destination's page in <paramref name="document"/>, or -1 when it is not one of its pages.</summary>
    public int GetPageIndex(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (PageReference is null)
        {
            return PageIndex is int index && index < document.Pages.Count ? index : -1;
        }

        return PageReference.Resolve() is PdfDictionary page ? document.Pages.IndexOf(new PdfPage(page, PageReference, document)) : -1;
    }

    private static PdfObject Number(double value)
    {
        return value == Math.Floor(value) && Math.Abs(value) < 1e15 ? new PdfInteger((long)value) : new PdfReal(value);
    }
}
