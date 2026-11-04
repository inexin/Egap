namespace Egap.Documents.Rtf;

/// <summary>
/// What the RTF reader kept on a node, under the format name "rtf", so the RTF writer can put it back: groups marked
/// <c>\*</c> that the reader does not know (Word's theme, data store and latent styles, say), raw, where they sat.
/// </summary>
internal sealed class RtfExtension
{
    /// <summary>The format name extension data is kept under.</summary>
    public const string Format = "rtf";

    /// <summary>On the document: the groups that came before the body, in file order.</summary>
    public List<string>? Groups { get; set; }

    /// <summary>On a paragraph: the groups read inside it, each with the number of inlines before it.</summary>
    public List<(int Index, string Markup)>? Inline { get; set; }

    /// <summary>The data kept on <paramref name="node"/>, made when there is none.</summary>
    public static RtfExtension Of(Node node)
    {
        if (node.Extensions.Get<RtfExtension>(Format) is { } extension)
        {
            return extension;
        }

        extension = new RtfExtension();
        node.Extensions.Set(Format, extension);
        return extension;
    }
}
