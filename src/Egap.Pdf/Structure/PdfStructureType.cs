namespace Egap.Pdf;

/// <summary>
/// A structure element type (ISO 32000-1 14.8.4, Phase 5 design Q14): one of the standard types, or a document's own
/// type that its role map maps to a standard one. Any name converts to a type.
/// </summary>
/// <param name="Name">The type's name, as <c>/S</c> holds it.</param>
public readonly record struct PdfStructureType(string Name)
{
    /// <summary>The whole document.</summary>
    public static PdfStructureType Document { get; } = new("Document");

    /// <summary>A large division of a document.</summary>
    public static PdfStructureType Part { get; } = new("Part");

    /// <summary>A self-contained body of text, such as an article.</summary>
    public static PdfStructureType Art { get; } = new("Art");

    /// <summary>A section.</summary>
    public static PdfStructureType Sect { get; } = new("Sect");

    /// <summary>A generic block grouping.</summary>
    public static PdfStructureType Div { get; } = new("Div");

    /// <summary>A block quotation.</summary>
    public static PdfStructureType BlockQuote { get; } = new("BlockQuote");

    /// <summary>A caption.</summary>
    public static PdfStructureType Caption { get; } = new("Caption");

    /// <summary>A table of contents.</summary>
    public static PdfStructureType TOC { get; } = new("TOC");

    /// <summary>An item of a table of contents.</summary>
    public static PdfStructureType TOCI { get; } = new("TOCI");

    /// <summary>An index.</summary>
    public static PdfStructureType Index { get; } = new("Index");

    /// <summary>A grouping with no structure of its own.</summary>
    public static PdfStructureType NonStruct { get; } = new("NonStruct");

    /// <summary>Content private to the producer.</summary>
    public static PdfStructureType Private { get; } = new("Private");

    /// <summary>A heading with no level.</summary>
    public static PdfStructureType H { get; } = new("H");

    /// <summary>A first-level heading.</summary>
    public static PdfStructureType H1 { get; } = new("H1");

    /// <summary>A second-level heading.</summary>
    public static PdfStructureType H2 { get; } = new("H2");

    /// <summary>A third-level heading.</summary>
    public static PdfStructureType H3 { get; } = new("H3");

    /// <summary>A fourth-level heading.</summary>
    public static PdfStructureType H4 { get; } = new("H4");

    /// <summary>A fifth-level heading.</summary>
    public static PdfStructureType H5 { get; } = new("H5");

    /// <summary>A sixth-level heading.</summary>
    public static PdfStructureType H6 { get; } = new("H6");

    /// <summary>A paragraph.</summary>
    public static PdfStructureType P { get; } = new("P");

    /// <summary>A list.</summary>
    public static PdfStructureType L { get; } = new("L");

    /// <summary>A list item.</summary>
    public static PdfStructureType LI { get; } = new("LI");

    /// <summary>A list item's label, such as its bullet or number.</summary>
    public static PdfStructureType Lbl { get; } = new("Lbl");

    /// <summary>A list item's body.</summary>
    public static PdfStructureType LBody { get; } = new("LBody");

    /// <summary>A table.</summary>
    public static PdfStructureType Table { get; } = new("Table");

    /// <summary>A table row.</summary>
    public static PdfStructureType TR { get; } = new("TR");

    /// <summary>A table header cell.</summary>
    public static PdfStructureType TH { get; } = new("TH");

    /// <summary>A table data cell.</summary>
    public static PdfStructureType TD { get; } = new("TD");

    /// <summary>A table's header rows.</summary>
    public static PdfStructureType THead { get; } = new("THead");

    /// <summary>A table's body rows.</summary>
    public static PdfStructureType TBody { get; } = new("TBody");

    /// <summary>A table's footer rows.</summary>
    public static PdfStructureType TFoot { get; } = new("TFoot");

    /// <summary>A generic inline span.</summary>
    public static PdfStructureType Span { get; } = new("Span");

    /// <summary>An inline quotation.</summary>
    public static PdfStructureType Quote { get; } = new("Quote");

    /// <summary>A note, such as a footnote.</summary>
    public static PdfStructureType Note { get; } = new("Note");

    /// <summary>A reference to elsewhere in the document.</summary>
    public static PdfStructureType Reference { get; } = new("Reference");

    /// <summary>A bibliography entry.</summary>
    public static PdfStructureType BibEntry { get; } = new("BibEntry");

    /// <summary>Computer code.</summary>
    public static PdfStructureType Code { get; } = new("Code");

    /// <summary>A link, holding the link annotation.</summary>
    public static PdfStructureType Link { get; } = new("Link");

    /// <summary>An annotation other than a link or widget.</summary>
    public static PdfStructureType Annot { get; } = new("Annot");

    /// <summary>A figure: an image or drawing, which needs alternate text.</summary>
    public static PdfStructureType Figure { get; } = new("Figure");

    /// <summary>A mathematical formula.</summary>
    public static PdfStructureType Formula { get; } = new("Formula");

    /// <summary>A form field, holding the widget annotation.</summary>
    public static PdfStructureType Form { get; } = new("Form");

    /// <summary>The standard type names (ISO 32000-1 14.8.4), against which role maps resolve.</summary>
    public static IReadOnlySet<string> StandardNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Document", "Part", "Art", "Sect", "Div", "BlockQuote", "Caption", "TOC", "TOCI", "Index", "NonStruct", "Private",
        "H", "H1", "H2", "H3", "H4", "H5", "H6", "P", "L", "LI", "Lbl", "LBody", "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot",
        "Span", "Quote", "Note", "Reference", "BibEntry", "Code", "Link", "Annot", "Ruby", "RB", "RT", "RP", "Warichu", "WT", "WP",
        "Figure", "Formula", "Form",
    };

    /// <summary>Whether the name is one of the standard types.</summary>
    public bool IsStandard => StandardNames.Contains(Name ?? "");

    /// <summary>The type with a name.</summary>
    public static implicit operator PdfStructureType(string name) => new(name);

    /// <summary>The type with a name.</summary>
    public static PdfStructureType FromString(string name) => new(name);

    /// <summary>The type's name.</summary>
    public override string ToString() => Name ?? "";
}
