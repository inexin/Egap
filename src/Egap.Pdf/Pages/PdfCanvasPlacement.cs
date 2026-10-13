namespace Egap.Pdf;

/// <summary>Where new drawing goes on a page that already has content (Phase 3 design Q5).</summary>
public enum PdfCanvasPlacement
{
    /// <summary>Over the page's content, drawn after it.</summary>
    Over,

    /// <summary>Under the page's content, drawn before it.</summary>
    Under,
}
