namespace Egap.Pdf.Rendering;

/// <summary>
/// Optional content in the content stream (8.11.3): a <c>/OC</c> marked-content sequence whose group or membership dictionary
/// is hidden hides everything up to its <c>EMC</c>, forms and images included; XObjects and annotations with a hidden
/// <c>/OC</c> are skipped where they are drawn.
/// </summary>
internal sealed partial class PageRenderer
{
    /// <summary>For each open marked-content sequence, whether it hid what it marks.</summary>
    private readonly Stack<bool> hiddenMarks = new();

    /// <summary>How many open sequences hide their content; nothing is drawn while above 0.</summary>
    private int hidden;

    protected override void OnBeginMarkedContent(PdfMarkedContent markedContent)
    {
        bool hides = settings.Layers is OptionalContent layers && markedContent.OptionalContent is PdfDictionary oc && !layers.IsVisible(oc);
        hiddenMarks.Push(hides);
        if (hides)
        {
            hidden++;
        }
    }

    protected override void OnEndMarkedContent(PdfMarkedContent markedContent)
    {
        if (hiddenMarks.TryPop(out bool hid) && hid)
        {
            hidden--;
        }
    }
}
