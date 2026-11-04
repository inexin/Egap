using System.Xml.Linq;

namespace Egap.Documents.Odf;

/// <summary>
/// What the ODT reader kept on a node, under the format name "odt", for the ODT writer to put back: markup the model has no
/// place for, where it sat, and on the document the package it came from.
/// </summary>
internal sealed class OdtExtension
{
    /// <summary>The format name extension data is kept under.</summary>
    public const string Format = "odt";

    /// <summary>On the document: the package read, for the files and settings the model does not hold.</summary>
    public OdfPackage? Package { get; set; }

    /// <summary>On the document: declarations of the body (sequences, variables, user fields, forms), in file order.</summary>
    public List<XElement>? Declarations { get; set; }

    /// <summary>On a block: elements the reader does not know that came right before it.</summary>
    public List<XElement>? Before { get; set; }

    /// <summary>On a paragraph: inline elements the reader does not know, each with the number of inlines before it.</summary>
    public List<(int Index, XElement Element)>? Inline { get; set; }

    /// <summary>The data kept on <paramref name="node"/>, made when there is none.</summary>
    public static OdtExtension Of(Node node)
    {
        if (node.Extensions.Get<OdtExtension>(Format) is { } extension)
        {
            return extension;
        }

        extension = new OdtExtension();
        node.Extensions.Set(Format, extension);
        return extension;
    }
}
