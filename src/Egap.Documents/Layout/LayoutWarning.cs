namespace Egap.Documents.Layout;

/// <summary>What a layout laid out differently from what the document asks for.</summary>
public enum LayoutWarningKind
{
    /// <summary>
    /// A floating table or a paragraph frame laid out in the text instead of floating: only those in a section's body float,
    /// so one in a table cell, header, footer, note, text box or margin box takes its place in the text there.
    /// </summary>
    FloatLaidOutInline,
}

/// <summary>Something a layout laid out differently from what the document asks for, with the node and page it is on.</summary>
/// <param name="Kind">What was laid out differently.</param>
/// <param name="Message">What, in words.</param>
/// <param name="Node">The node laid out differently, such as the table or the framed paragraph.</param>
/// <param name="PageIndex">The 0-based index of the first page it is on.</param>
public sealed record LayoutWarning(LayoutWarningKind Kind, string Message, Node Node, int PageIndex)
{
    /// <summary>The warning as one line, with its page.</summary>
    public override string ToString()
    {
        return $"{Kind}: {Message} (page {PageIndex + 1})";
    }
}
