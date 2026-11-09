using System.Globalization;
using System.Xml;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Docx;
using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets.Xlsx;

/// <summary>
/// The values a workbook keeps for a linked workbook (ECMA-376 Part 1, 18.14), which is what Excel calculates from when links
/// are not updated: <c>[1]Sheet!A1</c> reads the first book's cached sheet, and <c>[1]!Item</c> a name of the book or an item of a DDE link. Read when a formula first asks, read-only; the
/// part itself stays in the package and saves as it was. A book keeps no blanks, so a cell it doesn't hold reads as blank. A part that can't be read holds nothing, so what reads it shows
/// #REF!, and the workbook still loads.
/// </summary>
internal sealed class ExternalBook(Workbook book, OpcPart? part)
{
    private readonly Lazy<Contents> contents = new(() => Read(book, part));

    /// <summary>A linked book made in memory, such as one an ODS file refers to, which the workbook writes as a new link.</summary>
    /// <param name="book">The workbook that links to it.</param>
    /// <param name="target">The linked file, as the link's target.</param>
    public ExternalBook(Workbook book, string target)
        : this(book, (OpcPart?)null)
    {
        Target = target;
        contents.Value.IsBook = true;
    }

    /// <summary>The linked file of a book made in memory; null for one read from a package, whose part saves as it was.</summary>
    public string? Target { get; }

    /// <summary>The cached sheets of a book made in memory, in order.</summary>
    public List<Worksheet> MadeSheets { get; } = [];

    /// <summary>
    /// The linked file: the target of a book made in memory, or of the link part's <c>externalLinkPath</c> relationship;
    /// null for a DDE or OLE link, or a part without one.
    /// </summary>
    public string? File => Target ?? part?.Relationships.FirstOrDefault(r => r.External
        && (r.Type.EndsWith("/externalLinkPath", StringComparison.OrdinalIgnoreCase) || r.Type.EndsWith("/xlPathMissing", StringComparison.OrdinalIgnoreCase)))?.Target;

    /// <summary>The cached sheets, in the book's order.</summary>
    public IReadOnlyList<Worksheet> Sheets => MadeSheets.Count > 0 ? MadeSheets : contents.Value.Order;

    /// <summary>The cached sheet called <paramref name="name"/> of a book made in memory, added empty when it has none.</summary>
    public Worksheet MakeSheet(Workbook owner, string name)
    {
        if (!contents.Value.Sheets.TryGetValue(name, out Worksheet? sheet))
        {
            sheet = new Worksheet(owner, name, linked: true);
            contents.Value.Sheets[name] = sheet;
            MadeSheets.Add(sheet);
        }

        return sheet;
    }

    /// <summary>The cached sheet called <paramref name="name"/>, or null.</summary>
    public Worksheet? Sheet(string name) => contents.Value.Sheets.GetValueOrDefault(name);

    /// <summary>What a defined name of the linked book refers to, a reference within it, or null.</summary>
    public ReferenceNode? Name(string name) => contents.Value.Names.GetValueOrDefault(name);

    /// <summary>The value kept for an item of a DDE link, its first if it keeps several, or null.</summary>
    public CellValue? DdeItem(string name)
    {
        string item = name.StartsWith('\'') ? name[1..^1].Replace("''", "'", StringComparison.Ordinal) : name;
        return contents.Value.DdeItems.TryGetValue(item, out CellValue value) ? value : default(CellValue?);
    }

    /// <summary>
    /// What a name that <see cref="Name"/> and <see cref="DdeItem"/> don't have gives: #NAME? for one the book doesn't define
    /// a reference for, #REF! for one defined as something else, and for a DDE link or a part that can't be read.
    /// </summary>
    public CellError MissingName(string name) => contents.Value.IsBook && !contents.Value.OtherNames.Contains(name) ? CellError.Name : CellError.Reference;

    private static Contents Read(Workbook book, OpcPart? part)
    {
        Contents contents = new();
        if (part is null)
        {
            return contents;
        }

        try
        {
            XNamespace ns = XlsxNames.Main;
            XElement? root = part.LoadXml().Root;
            if (root?.Element(ns + "externalBook") is { } external)
            {
                contents.IsBook = true;
                ReadSheets(book, external, ns, contents);
                foreach (XElement name in external.Element(ns + "definedNames")?.Elements(ns + "definedName") ?? [])
                {
                    if ((string?)name.Attribute("name") is not { } key || (string?)name.Attribute("refersTo") is not { } refersTo)
                    {
                        continue;
                    }

                    if (FormulaParser.TryParse(refersTo.TrimStart('='), r1c1: false, default)?.Root is ReferenceNode { Prefix.FirstSheet: not null } reference)
                    {
                        contents.Names.TryAdd(key, reference);
                    }
                    else
                    {
                        contents.OtherNames.Add(key);
                    }
                }
            }

            foreach (XElement item in root?.Element(ns + "ddeLink")?.Element(ns + "ddeItems")?.Elements(ns + "ddeItem") ?? [])
            {
                if ((string?)item.Attribute("name") is { } key && item.Element(ns + "values")?.Element(ns + "value") is { } first && Value(first, ns + "val") is { } value)
                {
                    contents.DdeItems.TryAdd(key, value);
                }
            }
        }
        catch (Exception error) when (error is DocumentException or XmlException)
        {
            return new Contents();
        }

        return contents;
    }

    private static void ReadSheets(Workbook book, XElement external, XNamespace ns, Contents contents)
    {
        List<Worksheet> sheets = [];
        foreach (XElement sheetName in external.Element(ns + "sheetNames")?.Elements(ns + "sheetName") ?? [])
        {
            Worksheet sheet = new(book, (string?)sheetName.Attribute("val") ?? "", linked: true);
            sheets.Add(sheet);
            if (contents.Sheets.TryAdd(sheet.Name, sheet))
            {
                contents.Order.Add(sheet);
            }
        }

        foreach (XElement data in external.Element(ns + "sheetDataSet")?.Elements(ns + "sheetData") ?? [])
        {
            if (!int.TryParse((string?)data.Attribute("sheetId"), NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id >= sheets.Count)
            {
                continue;
            }

            sheets[id].LinkRefreshFailed = XlsxNames.Bool((string?)data.Attribute("refreshError"), false);
            foreach (XElement cell in data.Elements(ns + "row").Elements(ns + "cell"))
            {
                if (CellAddress.TryParse((string?)cell.Attribute("r"), out CellAddress address) && Value(cell, ns + "v") is { } value)
                {
                    sheets[id].SetValue(address.Row, address.Column, value);
                }
            }
        }
    }

    private static CellValue? Value(XElement cell, XName holder)
    {
        if ((string?)cell.Element(holder) is not { } text)
        {
            return null;
        }

        switch ((string?)cell.Attribute("t"))
        {
            case "b":
                return CellValue.FromBoolean(text is "1" or "true");
            case "e":
                return CellValue.TryParseError(text, out CellError error) ? CellValue.FromError(error) : null;
            case "str":
                return CellValue.TextUnchecked(text.Length <= CellValue.MaxTextLength ? text : text[..CellValue.MaxTextLength]);
            default:
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : null;
        }
    }

    private sealed class Contents
    {
        public Dictionary<string, Worksheet> Sheets { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<Worksheet> Order { get; } = [];

        public bool IsBook { get; set; }

        public Dictionary<string, ReferenceNode> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> OtherNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, CellValue> DdeItems { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
