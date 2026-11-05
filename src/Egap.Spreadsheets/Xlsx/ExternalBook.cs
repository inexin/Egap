using System.Globalization;
using System.Xml;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Docx;
using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets.Xlsx;

/// <summary>
/// The values a workbook keeps for a linked workbook (ECMA-376 Part 1, 18.14), which is what Excel calculates from when links
/// are not updated: <c>[1]Sheet!A1</c> reads the first book's cached sheet. Read when a formula first asks, read-only; the
/// part itself stays in the package and saves as it was. A part that can't be read holds nothing, so what reads it shows
/// #REF!, and the workbook still loads.
/// </summary>
internal sealed class ExternalBook(Workbook book, OpcPart? part)
{
    private readonly Lazy<Contents> contents = new(() => Read(book, part));

    /// <summary>The cached sheet called <paramref name="name"/>, or null.</summary>
    public Worksheet? Sheet(string name) => contents.Value.Sheets.GetValueOrDefault(name);

    /// <summary>What a defined name of the linked book refers to, a reference within it, or null.</summary>
    public ReferenceNode? Name(string name) => contents.Value.Names.GetValueOrDefault(name);

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
            if (part.LoadXml().Root?.Element(ns + "externalBook") is { } external)
            {
                ReadSheets(book, external, ns, contents);
                foreach (XElement name in external.Element(ns + "definedNames")?.Elements(ns + "definedName") ?? [])
                {
                    if ((string?)name.Attribute("name") is { } key && (string?)name.Attribute("refersTo") is { } refersTo
                        && FormulaParser.TryParse(refersTo.TrimStart('='), r1c1: false, default)?.Root is ReferenceNode { Prefix.FirstSheet: not null } reference)
                    {
                        contents.Names.TryAdd(key, reference);
                    }
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
            Worksheet sheet = new(book, (string?)sheetName.Attribute("val") ?? "");
            sheets.Add(sheet);
            contents.Sheets.TryAdd(sheet.Name, sheet);
        }

        foreach (XElement data in external.Element(ns + "sheetDataSet")?.Elements(ns + "sheetData") ?? [])
        {
            if (!int.TryParse((string?)data.Attribute("sheetId"), NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id >= sheets.Count)
            {
                continue;
            }

            foreach (XElement cell in data.Elements(ns + "row").Elements(ns + "cell"))
            {
                if (CellAddress.TryParse((string?)cell.Attribute("r"), out CellAddress address) && Value(cell, ns) is { } value)
                {
                    sheets[id].SetValue(address.Row, address.Column, value);
                }
            }
        }
    }

    private static CellValue? Value(XElement cell, XNamespace ns)
    {
        if ((string?)cell.Element(ns + "v") is not { } text)
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

        public Dictionary<string, ReferenceNode> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
