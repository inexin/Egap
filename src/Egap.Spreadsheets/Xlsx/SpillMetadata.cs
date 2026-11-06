using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Docx;

namespace Egap.Spreadsheets.Xlsx;

/// <summary>
/// The workbook's cell metadata part, which marks dynamic array formulas ([MS-XLSX] XLDAPR): a formula's cell names the entry
/// in <c>cellMetadata</c> that says the array is dynamic.
/// </summary>
internal static class SpillMetadata
{
    private const string TypeName = "XLDAPR";
    private const string FutureUri = "{bdbb8cdc-fa1e-496e-a857-3c3f30c029c3}";
    private static readonly XNamespace Dynamic = "http://schemas.microsoft.com/office/spreadsheetml/2017/dynamicarray";

    /// <summary>The part with the mark in it, added to what the file's part held, and the number a cell names it by.</summary>
    public static (byte[] Part, int Mark) Mark(byte[]? existing)
    {
        XNamespace ns = XlsxNames.MainUri;
        XDocument document = existing is null ? new XDocument(new XElement(ns + "metadata", new XAttribute(XNamespace.Xmlns + "xda", Dynamic))) : SafeXml.Load(existing, new DocumentLimits());
        XElement root = document.Root!;

        XElement types = Child(root, ns + "metadataTypes", atStart: true);
        List<XElement> known = [.. types.Elements(ns + "metadataType")];
        int type = known.FindIndex(t => (string?)t.Attribute("name") == TypeName) + 1;
        if (type == 0)
        {
            types.Add(new XElement(ns + "metadataType",
                new XAttribute("name", TypeName), new XAttribute("minSupportedVersion", "120000"), new XAttribute("copy", "1"),
                new XAttribute("pasteAll", "1"), new XAttribute("pasteValues", "1"), new XAttribute("merge", "1"),
                new XAttribute("splitFirst", "1"), new XAttribute("rowColShift", "1"), new XAttribute("clearFormats", "1"),
                new XAttribute("clearComments", "1"), new XAttribute("assign", "1"), new XAttribute("coerce", "1"), new XAttribute("cellMeta", "1")));
            type = known.Count + 1;
            types.SetAttributeValue("count", XlsxNames.Integer(type));
        }

        XElement? future = root.Elements(ns + "futureMetadata").FirstOrDefault(f => (string?)f.Attribute("name") == TypeName);
        if (future is null)
        {
            future = new XElement(ns + "futureMetadata", new XAttribute("name", TypeName), new XAttribute("count", "0"));
            types.AddAfterSelf(future);
        }

        List<XElement> blocks = [.. future.Elements(ns + "bk")];
        int value = blocks.FindIndex(b => b.Descendants(Dynamic + "dynamicArrayProperties").Any(p => (string?)p.Attribute("fDynamic") == "1"));
        if (value < 0)
        {
            value = blocks.Count;
            future.Add(new XElement(ns + "bk", new XElement(ns + "extLst", new XElement(ns + "ext", new XAttribute("uri", FutureUri),
                new XElement(Dynamic + "dynamicArrayProperties", new XAttribute("fDynamic", "1"), new XAttribute("fCollapsed", "0"))))));
            future.SetAttributeValue("count", XlsxNames.Integer(value + 1));
        }

        XElement cells = root.Element(ns + "cellMetadata") ?? AddCellMetadata(root, ns);
        List<XElement> entries = [.. cells.Elements(ns + "bk")];
        string typeText = XlsxNames.Integer(type), valueText = XlsxNames.Integer(value);
        int mark = entries.FindIndex(b => b.Elements(ns + "rc").Any(r => (string?)r.Attribute("t") == typeText && (string?)r.Attribute("v") == valueText)) + 1;
        if (mark == 0)
        {
            cells.Add(new XElement(ns + "bk", new XElement(ns + "rc", new XAttribute("t", typeText), new XAttribute("v", valueText))));
            mark = entries.Count + 1;
            cells.SetAttributeValue("count", XlsxNames.Integer(mark));
        }

        return (OpcPackageWriter.Serialize(document), mark);
    }

    private static XElement Child(XElement root, XName name, bool atStart)
    {
        if (root.Element(name) is { } found)
        {
            return found;
        }

        XElement added = new(name, new XAttribute("count", "0"));
        if (atStart)
        {
            root.AddFirst(added);
        }
        else
        {
            root.Add(added);
        }

        return added;
    }

    /// <summary>Adds the cell metadata in its place: after the future metadata, before the value metadata.</summary>
    private static XElement AddCellMetadata(XElement root, XNamespace ns)
    {
        XElement cells = new(ns + "cellMetadata", new XAttribute("count", "0"));
        root.Elements(ns + "futureMetadata").Last().AddAfterSelf(cells);
        return cells;
    }
}
