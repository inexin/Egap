namespace Egap.Documents;

/// <summary>How tracked changes show on pages, in images and PDFs, and in the HTML, EPUB and Markdown written.</summary>
public enum RevisionView
{
    /// <summary>As if every change were accepted: inserted text shows, deleted text does not. The default.</summary>
    Final,

    /// <summary>
    /// Every change shows, in its author's colour: inserted text underlined, deleted text struck through and still taking
    /// its space, text whose formatting changed in its new look with a dotted underline, and a bar in the outside margin
    /// beside each line with a change.
    /// </summary>
    Markup,

    /// <summary>As if every change were rejected: the document before the changes.</summary>
    Original,
}
