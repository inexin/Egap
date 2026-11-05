using System.Xml.Linq;

using Egap.Documents.Docx;

namespace Egap.Spreadsheets.Xlsx;

/// <summary>What a loaded workbook's package held, kept so a save writes back what the model does not hold.</summary>
internal sealed class WorkbookSource(OpcPackage package, OpcPart workbookPart, XDocument workbookXml)
{
    public OpcPackage Package { get; } = package;

    public OpcPart WorkbookPart { get; } = workbookPart;

    /// <summary>The workbook part as read; a save writes a copy with the sheets, names and settings the model holds.</summary>
    public XDocument WorkbookXml { get; } = workbookXml;

    /// <summary>The sheets that are not worksheets (chart sheets, dialog sheets, macro sheets), kept with their parts.</summary>
    public List<KeptSheet> KeptSheets { get; } = [];

    /// <summary>Whether a cell held a formula when loaded; a save after changes then asks Excel to calculate again.</summary>
    public bool HadFormulas { get; set; }
}

/// <summary>A sheet entry of the workbook part that is not a worksheet, with the worksheet it came after.</summary>
internal sealed class KeptSheet(XElement element, Worksheet? after)
{
    /// <summary>The workbook part's sheet element.</summary>
    public XElement Element { get; } = element;

    /// <summary>The worksheet before it in tab order, or null when it came first.</summary>
    public Worksheet? After { get; } = after;
}

/// <summary>What a loaded worksheet's part held that the model does not, and where the part was.</summary>
internal sealed class SheetSource(OpcPart part, string relationshipId, int sheetId)
{
    public OpcPart Part { get; } = part;

    public string RelationshipId { get; } = relationshipId;

    public int SheetId { get; } = sheetId;

    /// <summary>The worksheet element's attributes, namespace declarations among them.</summary>
    public List<XAttribute> RootAttributes { get; } = [];

    /// <summary>The elements the model does not hold, in file order, with the place each takes in the worksheet.</summary>
    public List<(double Rank, XElement Element)> Kept { get; } = [];

    /// <summary>The file's sheetPr, sheetFormatPr and sheetView elements, written back with what the model holds set in them.</summary>
    public XElement? SheetProperties { get; set; }

    public XElement? SheetFormat { get; set; }

    public XElement? SheetViews { get; set; }

    /// <summary>The drawing element and its part, written back as they were while no object changes.</summary>
    public (XElement Element, OpcPart Part)? Drawing { get; set; }

    /// <summary>The legacyDrawing element and the comments part, written back as they were while no note changes.</summary>
    public XElement? LegacyDrawing { get; set; }

    public OpcPart? CommentsPart { get; set; }

    public OpcPart? VmlPart { get; set; }

    /// <summary>The tableParts element's own attributes.</summary>
    public XElement? TableParts { get; set; }

    /// <summary>The hyperlinks element's own attributes.</summary>
    public XElement? Hyperlinks { get; set; }
}

/// <summary>A loaded workbook's style part: the document, and the elements of the tables the model writes again.</summary>
internal sealed class StyleSource(XDocument document)
{
    public XDocument Document { get; } = document;

    public List<XElement> Fonts { get; } = [];

    public List<XElement> Fills { get; } = [];

    public List<XElement> Borders { get; } = [];

    public List<XElement> CellFormats { get; } = [];
}

/// <summary>The place each element takes in a worksheet part, as CT_Worksheet orders them (ECMA-376 Part 1, 18.3.1.99).</summary>
internal static class WorksheetOrder
{
    private static readonly string[] Names =
    [
        "sheetPr", "dimension", "sheetViews", "sheetFormatPr", "cols", "sheetData", "sheetCalcPr", "sheetProtection",
        "protectedRanges", "scenarios", "autoFilter", "sortState", "dataConsolidate", "customSheetViews", "mergeCells",
        "phoneticPr", "conditionalFormatting", "dataValidations", "hyperlinks", "printOptions", "pageMargins", "pageSetup",
        "headerFooter", "rowBreaks", "colBreaks", "customProperties", "cellWatches", "ignoredErrors", "smartTags", "drawing",
        "legacyDrawing", "legacyDrawingHF", "drawingHF", "picture", "oleObjects", "controls", "webPublishItems", "tableParts",
        "extLst",
    ];

    private static readonly Dictionary<string, int> Ranks = Names.Select((name, i) => (name, i)).ToDictionary(p => p.name, p => p.i);

    /// <summary>The rank of a SpreadsheetML element name, or -1 for an element of another namespace or one CT_Worksheet does not name.</summary>
    public static int Rank(XName name) => XlsxNames.IsMain(name.NamespaceName) && Ranks.TryGetValue(name.LocalName, out int rank) ? rank : -1;

    public static int Rank(string localName) => Ranks[localName];
}

/// <summary>The place each element takes in a workbook part, as CT_Workbook orders them (ECMA-376 Part 1, 18.2.27).</summary>
internal static class WorkbookOrder
{
    private static readonly string[] Names =
    [
        "fileVersion", "fileSharing", "workbookPr", "workbookProtection", "bookViews", "sheets", "functionGroups",
        "externalReferences", "definedNames", "calcPr", "oleSize", "customWorkbookViews", "pivotCaches", "smartTagPr",
        "smartTagTypes", "webPublishing", "fileRecoveryPr", "webPublishObjects", "extLst",
    ];

    /// <summary>Puts <paramref name="element"/> into <paramref name="parent"/>, replacing one of its name or else before the first child that comes after it.</summary>
    public static void Place(XElement parent, XElement element)
    {
        if (parent.Element(element.Name) is { } old)
        {
            old.ReplaceWith(element);
            return;
        }

        int rank = Array.IndexOf(Names, element.Name.LocalName);
        foreach (XElement child in parent.Elements())
        {
            int other = Array.IndexOf(Names, child.Name.LocalName);
            if (child.Name.Namespace == element.Name.Namespace && other > rank)
            {
                child.AddBeforeSelf(element);
                return;
            }
        }

        parent.Add(element);
    }
}
