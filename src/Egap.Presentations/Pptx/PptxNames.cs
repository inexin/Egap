using System.Globalization;
using System.Xml.Linq;

namespace Egap.Presentations.Pptx;

/// <summary>The XML namespaces and relationship types PresentationML uses, in their transitional spelling.</summary>
internal static class PptxNames
{
    public static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static readonly XNamespace P14 = "http://schemas.microsoft.com/office/powerpoint/2010/main";
    public static readonly XNamespace P15 = "http://schemas.microsoft.com/office/powerpoint/2012/main";
    public static readonly XNamespace A14 = "http://schemas.microsoft.com/office/drawing/2010/main";
    public static readonly XNamespace A16 = "http://schemas.microsoft.com/office/drawing/2014/main";

    private const string Office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

    public const string SlideRelationship = Office + "slide";
    public const string SlideLayoutRelationship = Office + "slideLayout";
    public const string SlideMasterRelationship = Office + "slideMaster";
    public const string NotesSlideRelationship = Office + "notesSlide";
    public const string NotesMasterRelationship = Office + "notesMaster";
    public const string ThemeRelationship = Office + "theme";

    public const string TableUri = "http://schemas.openxmlformats.org/drawingml/2006/table";
    public const string ChartUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    public const string ChartExUri = "http://schemas.microsoft.com/office/drawing/2014/chartex";
    public const string DiagramUri = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    public const string OleUri = "http://schemas.openxmlformats.org/presentationml/2006/ole";

    /// <summary>The namespaces the reader understands, for markup compatibility choices.</summary>
    public static bool Understood(string ns)
    {
        return ns == P.NamespaceName || ns == A.NamespaceName || ns == R.NamespaceName || ns == P14.NamespaceName
            || ns == P15.NamespaceName || ns == A14.NamespaceName || ns == A16.NamespaceName;
    }

    /// <summary>An integer attribute, or null when it is missing or not a number.</summary>
    public static long? Long(XElement? element, XName name)
    {
        return long.TryParse((string?)element?.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;
    }

    /// <summary>An unsigned integer attribute, or null when it is missing or not a number.</summary>
    public static uint? UInt(XElement? element, XName name)
    {
        return uint.TryParse((string?)element?.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint value) ? value : null;
    }

    /// <summary>An xsd:boolean attribute, or <paramref name="fallback"/> when it is missing or not a boolean.</summary>
    public static bool Bool(XElement? element, XName name, bool fallback = false)
    {
        return (string?)element?.Attribute(name) switch
        {
            "1" or "true" => true,
            "0" or "false" => false,
            _ => fallback,
        };
    }

    /// <summary>A number in XML's invariant form.</summary>
    public static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
