using System.Globalization;
using System.Xml.Linq;

namespace Egap.Documents.Docx;

/// <summary>The XML namespaces DOCX uses, in their transitional spelling.</summary>
internal static class Ns
{
    public static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    public static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    public static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    public static readonly XNamespace O = "urn:schemas-microsoft-com:office:office";
    public static readonly XNamespace W10 = "urn:schemas-microsoft-com:office:word";
    public static readonly XNamespace W14 = "http://schemas.microsoft.com/office/word/2010/wordml";
    public static readonly XNamespace W15 = "http://schemas.microsoft.com/office/word/2012/wordml";
    public static readonly XNamespace Wps = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    public static readonly XNamespace Wpg = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";
    public static readonly XNamespace Wp14 = "http://schemas.microsoft.com/office/word/2010/wordprocessingDrawing";
    public static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    public static readonly XNamespace Dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
    public static readonly XNamespace Cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    public static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    public static readonly XNamespace DcTerms = "http://purl.org/dc/terms/";
    public static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    public static readonly XNamespace Xml = XNamespace.Xml;

    /// <summary>The namespaces the reader understands, for markup compatibility choices.</summary>
    public static bool Understood(string ns)
    {
        return ns == W.NamespaceName || ns == R.NamespaceName || ns == Wp.NamespaceName || ns == A.NamespaceName || ns == Pic.NamespaceName
            || ns == Wps.NamespaceName || ns == Wp14.NamespaceName || ns == V.NamespaceName || ns == O.NamespaceName || ns == M.NamespaceName
            || ns == W14.NamespaceName;
    }
}

/// <summary>DOCX's units and simple types: twips, half-points, eighths of a point, EMUs, on and off.</summary>
internal static class DocxUnits
{
    public const double Twip = 1 / 20.0;
    public const double HalfPoint = 0.5;
    public const double EighthPoint = 1 / 8.0;
    public const double Emu = 1 / 12700.0;

    /// <summary>ST_OnOff: absent value or true, 1, on mean on; false, 0, off mean off.</summary>
    public static bool OnOff(XElement element)
    {
        string? value = (string?)element.Attribute(Ns.W + "val");
        return value is null or "true" or "1" or "on";
    }

    /// <summary>A twips measure (or one with units: pt, in, cm, mm, pc, pi) in points, or null.</summary>
    public static double? Points(string? value, double unit = Twip)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        (string Suffix, double Factor)[] units = [("pt", 1), ("in", 72), ("cm", 72 / 2.54), ("mm", 72 / 25.4), ("pc", 12), ("pi", 12)];
        foreach ((string suffix, double factor) in units)
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out double withUnit) && double.IsFinite(withUnit))
            {
                return withUnit * factor;
            }
        }

        // Dividing by the whole number of units per point keeps 1701 twips at 85.05 exactly, where multiplying by 0.05 does not.
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number / Math.Round(1 / unit) : null;
    }

    /// <summary>An integer attribute, or null.</summary>
    public static int? Int(string? value)
    {
        return value is not null && long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long number)
            ? (int)Math.Clamp(number, int.MinValue, int.MaxValue) : null;
    }

    /// <summary>Points as twips text.</summary>
    public static string Twips(double points)
    {
        return Math.Round(points * 20).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Points as half-points text.</summary>
    public static string HalfPoints(double points)
    {
        return Math.Round(points * 2).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Points as EMU text.</summary>
    public static string Emus(double points)
    {
        return Math.Round(points * 12700).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The w:val attribute of an element.</summary>
    public static string? Val(this XElement? element)
    {
        return (string?)element?.Attribute(Ns.W + "val");
    }

    /// <summary>A w: attribute's value.</summary>
    public static string? W(this XElement element, string name)
    {
        return (string?)element.Attribute(Ns.W + name);
    }
}

/// <summary>
/// What the DOCX reader keeps on a node for the writer (Phase 6 design Q7): the element as read and the node's version at
/// the end of loading, so an unchanged node is written back exactly; the attributes and children the model does not map,
/// with their place, for a changed one.
/// </summary>
internal sealed class DocxExtension
{
    public const string Format = "docx";

    /// <summary>The node's element as read.</summary>
    public XElement? Original { get; set; }

    /// <summary>The node's version when loading ended (a paragraph's or story's <c>Version</c>, a table's stamp).</summary>
    public long Version { get; set; } = -1;

    /// <summary>The part a header, footer or the document's main story came from, for the writer to reuse its name.</summary>
    public string? PartName { get; set; }

    /// <summary>For a block read from an mc:AlternateContent: that element, written back while the block is unchanged.</summary>
    public XElement? Wrapper { get; set; }

    /// <summary>The reading this came from, so a node imported from another document is written from the model.</summary>
    public DocxPackageData? Source { get; set; }

    /// <summary>For an inline read from inside a w:r (a tab, a field character, a drawing): that run's properties.</summary>
    public XElement? RunProperties { get; set; }

    /// <summary>Markup that sat between this block and the one before it (bookmarks, permission and move ranges).</summary>
    public List<XElement>? Before { get; set; }

    /// <summary>Markup that sat after this block at the end of its container.</summary>
    public List<XElement>? After { get; set; }

    /// <summary>For a mark the reader moved into a paragraph from between blocks: the writer puts it back from <see cref="Before"/>.</summary>
    public bool Hoisted { get; set; }

    /// <summary>For an island: whether its markup sat inside a w:r.</summary>
    public bool InRun { get; set; }

    /// <summary>What the node held when loading ended, where its version cannot tell (a picture's image, a section's setup).</summary>
    public object? Snapshot { get; set; }

    /// <summary>The element's attributes the model does not map, such as w14:paraId and the rsids.</summary>
    public List<XAttribute>? Attributes { get; set; }

    /// <summary>Child elements the model does not map, each with the number of mapped children before it.</summary>
    public List<(int Index, XElement Element)>? Children { get; set; }

    /// <summary>Unmapped children of property elements (pPr, rPr, tblPr and the like), by the property element's name.</summary>
    public Dictionary<string, List<XElement>>? UnknownProperties { get; set; }

    public static DocxExtension Of(Node node)
    {
        DocxExtension? extension = node.Extensions.Get<DocxExtension>(Format);
        if (extension is null)
        {
            extension = new DocxExtension();
            node.Extensions.Set(Format, extension);
        }

        return extension;
    }

    public void AddUnknownProperty(string container, XElement element)
    {
        UnknownProperties ??= [];
        if (!UnknownProperties.TryGetValue(container, out List<XElement>? list))
        {
            list = [];
            UnknownProperties[container] = list;
        }

        list.Add(element);
    }
}

/// <summary>
/// What the DOCX reader keeps on the document for the writer: the package as read (unknown parts, macros, custom XML and
/// relationships go back from here), the main part's name and the formatting version when loading ended, so unchanged
/// styles, numbering and settings parts are written back as they were.
/// </summary>
internal sealed class DocxPackageData
{
    public required OpcPackage Package { get; init; }

    public required string MainPartName { get; init; }

    public required string MainContentType { get; init; }

    /// <summary>The main part's root element as read, without children: its namespace declarations and attributes.</summary>
    public XElement? MainRoot { get; set; }

    /// <summary>The document's background (w:background), kept as read.</summary>
    public XElement? Background { get; set; }

    /// <summary>Other children of the main part's root after the body (a second body in a damaged file), kept as read.</summary>
    public List<XElement> RootExtras { get; } = [];

    /// <summary>The document's <see cref="Document.FormattingVersion"/> when loading ended.</summary>
    public long FormattingVersion { get; set; } = -1;

    /// <summary>Each style's element as read, by style id.</summary>
    public Dictionary<string, XElement> Styles { get; } = new(StringComparer.Ordinal);

    /// <summary>The note stories Word keeps outside the text (separators and continuation notices), by part.</summary>
    public List<XElement> SpecialNotes { get; } = [];

    /// <summary>The largest w:id in the story parts, so ids the writer gives new revisions do not repeat kept ones.</summary>
    public int MaxMarkupId { get; set; }

    /// <summary>Notes and comments no reference in the text points at, kept for the writer to put back.</summary>
    public List<XElement> Unreferenced { get; } = [];

    /// <summary>Parts the reader mapped into the model, which the writer writes from the model.</summary>
    public HashSet<string> MappedParts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static DocxPackageData? Of(Document document)
    {
        return document.Extensions.Get<DocxPackageData>(DocxExtension.Format);
    }
}

/// <summary>What a picture showed when it was read, so the writer can tell whether it changed.</summary>
internal sealed record PictureState(ImageData Image, double Width, double Height, string? Title, string? Description, FloatingPlacement? Floating, string? SourceUrl,
    (double, double, double, double) Crop)
{
    public static PictureState Of(Picture picture)
    {
        return new PictureState(picture.Image, picture.Width, picture.Height, picture.Title, picture.Description, picture.Floating, picture.SourceUrl, picture.Crop);
    }

    public bool Matches(Picture picture)
    {
        return ReferenceEquals(Image, picture.Image) && this == Of(picture) with { Image = Image };
    }
}

/// <summary>What a shape showed when it was read, so the writer can tell whether only its text changed.</summary>
internal sealed record ShapeState(ShapeKind Kind, double Width, double Height, DocumentColor? Fill, Border? Outline, FloatingPlacement? Floating, double Rotation, string? Title, string? Description)
{
    public static ShapeState Of(Shape shape)
    {
        return new ShapeState(shape.Kind, shape.Width, shape.Height, shape.Fill, shape.Outline, shape.Floating, shape.Rotation, shape.Title, shape.Description);
    }
}

/// <summary>What a content control held when it was read: its properties and its child nodes.</summary>
internal sealed record ControlState(ContentControlKind Kind, string? Tag, string? Title, bool LockContents, bool LockControl, bool ShowingPlaceholder, bool? IsChecked, Node[] Children)
{
    public static ControlState Of(ContentControlProperties properties, IEnumerable<Node> children)
    {
        return new ControlState(properties.Kind, properties.Tag, properties.Title, properties.LockContents, properties.LockControl, properties.ShowingPlaceholder, properties.IsChecked, [.. children]);
    }

    public bool Matches(ContentControlProperties properties, IEnumerable<Node> children)
    {
        ControlState now = Of(properties, children);
        return now with { Children = Children } == this && Children.SequenceEqual(now.Children);
    }
}
