namespace Egap.Pdf;

/// <summary>Which annotations flattening draws, as qpdf's <c>--flatten-annotations</c> chooses them; hidden ones never.</summary>
public enum PdfFlattenFilter
{
    /// <summary>Every annotation that is not hidden.</summary>
    All,

    /// <summary>Those with the print flag, as a printout shows them.</summary>
    Print,

    /// <summary>Those without the no-view flag, as a screen shows them.</summary>
    Screen,
}
