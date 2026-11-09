using System.Xml.Linq;

namespace Egap.Documents.Odf;

/// <summary>The XML namespaces of OpenDocument (ODF 1.3 Part 3, 3.2), and the names the ODT reader looks up most.</summary>
internal static class OdfNames
{
    public static readonly XNamespace OfficeNs = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    public static readonly XNamespace StyleNs = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    public static readonly XNamespace TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    public static readonly XNamespace TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    public static readonly XNamespace DrawNs = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    public static readonly XNamespace FoNs = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    public static readonly XNamespace XLinkNs = "http://www.w3.org/1999/xlink";
    public static readonly XNamespace DcNs = "http://purl.org/dc/elements/1.1/";
    public static readonly XNamespace MetaNs = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
    public static readonly XNamespace NumberNs = "urn:oasis:names:tc:opendocument:xmlns:datastyle:1.0";
    public static readonly XNamespace SvgNs = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";
    public static readonly XNamespace ConfigNs = "urn:oasis:names:tc:opendocument:xmlns:config:1.0";
    public static readonly XNamespace FormNs = "urn:oasis:names:tc:opendocument:xmlns:form:1.0";
    public static readonly XNamespace ManifestNs = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    public static readonly XNamespace XmlNs = XNamespace.Xml;
    public static readonly XNamespace MathMLNs = "http://www.w3.org/1998/Math/MathML";

    /// <summary>LibreOffice's extension namespace for features ODF does not have yet.</summary>
    public static readonly XNamespace LoExtNs = "urn:org:documentfoundation:names:experimental:office:xmlns:loext:1.0";

    /// <summary>The media type of a text document.</summary>
    public const string TextMediaType = "application/vnd.oasis.opendocument.text";

    /// <summary>The media type of a text document template.</summary>
    public const string TemplateMediaType = "application/vnd.oasis.opendocument.text-template";

    /// <summary>Whether <paramref name="mediaType"/> is a text document's or a text template's.</summary>
    public static bool IsText(string? mediaType) => mediaType is TextMediaType or TemplateMediaType;

    /// <summary>The media type of a spreadsheet.</summary>
    public const string SpreadsheetMediaType = "application/vnd.oasis.opendocument.spreadsheet";

    /// <summary>The media type of a spreadsheet template.</summary>
    public const string SpreadsheetTemplateMediaType = "application/vnd.oasis.opendocument.spreadsheet-template";

    /// <summary>Whether <paramref name="mediaType"/> is a spreadsheet's or a spreadsheet template's.</summary>
    public static bool IsSpreadsheet(string? mediaType) => mediaType is SpreadsheetMediaType or SpreadsheetTemplateMediaType;

    /// <summary>The attribute <paramref name="name"/> in <paramref name="ns"/>, or null.</summary>
    public static string? Attr(this XElement element, XNamespace ns, string name) => element.Attribute(ns + name)?.Value;
}
