using System.Globalization;
using System.Xml;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Docx;
using Egap.Spreadsheets.Storage;

namespace Egap.Spreadsheets.Xlsx;

/// <summary>
/// Reads an XLSX, XLSM, XLTX or XLTM package into a <see cref="Workbook"/> (ECMA-376 Part 1, chapter 18; MS-XLSX): the
/// workbook part's sheets, names and date system, the style part, the shared strings and each worksheet. Sheet data is read
/// as a stream; what the model does not hold is kept for the writer.
/// </summary>
internal static class XlsxReader
{
    private const string OfficeDocument = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";

    public static void Read(Workbook book, Stream stream, DocumentLimits limits)
    {
        OpcPackage package = OpcPackage.Read(stream, limits);
        OpcPart workbookPart = package.GetRelatedPart(package.Relationships, OfficeDocument)
            ?? throw new DocumentException("The package has no main document part.");
        WorkbookFormat format = XlsxNames.FormatOf(workbookPart.ContentType)
            ?? throw new DocumentException($"The package's main part is {workbookPart.ContentType}, not a workbook.");

        XDocument workbookXml = workbookPart.LoadXml();
        XNamespace ns = XlsxNames.Main;
        XElement root = workbookXml.Root is { } r && r.Name == ns + "workbook" ? r : throw new DocumentException("The workbook part has no workbook element.");
        WorkbookSource source = new(package, workbookPart, workbookXml);
        book.Source = source;
        book.Format = format;
        book.Date1904 = XlsxNames.Bool((string?)root.Element(ns + "workbookPr")?.Attribute("date1904"), false);
        ReadCalculation(book.Calculation, root.Element(ns + "calcPr"));

        XlsxStyles.Read(book.Styles, package.GetRelatedPart(workbookPart.Relationships, XlsxNames.StylesRelationship)?.LoadXml());
        if (package.GetRelatedPart(workbookPart.Relationships, XlsxNames.SharedStringsRelationship) is { } strings)
        {
            ReadSharedStrings(book.SharedStrings, strings, package);
        }

        // Each sheet element by position, for the names local to a sheet: a worksheet, or the index of a kept sheet.
        List<(Worksheet? Sheet, int Kept)> positions = [];
        Worksheet? last = null;
        foreach (XElement entry in root.Element(ns + "sheets")?.Elements(ns + "sheet") ?? [])
        {
            string id = (string?)entry.Attribute(XlsxNames.R + "id") ?? "";
            OpcRelationship? relationship = workbookPart.GetRelationship(id);
            OpcPart? part = relationship is { External: false } ? package.GetPart(relationship.TargetName) : null;
            if (part is null || relationship is null || !XlsxNames.SameType(relationship.Type, XlsxNames.WorksheetRelationship))
            {
                source.KeptSheets.Add(new KeptSheet(new XElement(entry), last));
                positions.Add((null, source.KeptSheets.Count - 1));
                continue;
            }

            Worksheet sheet = AddSheet(book, (string?)entry.Attribute("name"));
            sheet.Visibility = (string?)entry.Attribute("state") switch
            {
                "hidden" => SheetVisibility.Hidden,
                "veryHidden" => SheetVisibility.VeryHidden,
                _ => SheetVisibility.Visible,
            };
            sheet.Source = new SheetSource(part, id, XlsxNames.Int((string?)entry.Attribute("sheetId"), 0));
            ReadWorksheet(book, sheet, part, package, source);
            positions.Add((sheet, -1));
            last = sheet;
        }

        if (book.Worksheets.Count == 0 && source.KeptSheets.Count == 0)
        {
            throw new DocumentException("The workbook has no sheets.");
        }

        foreach (XElement element in root.Element(ns + "definedNames")?.Elements(ns + "definedName") ?? [])
        {
            ReadName(book, element, positions);
        }

        // Formulas number the linked books from 1, in this order.
        foreach (XElement link in root.Element(ns + "externalReferences")?.Elements(ns + "externalReference") ?? [])
        {
            OpcRelationship? relationship = workbookPart.GetRelationship((string?)link.Attribute(XlsxNames.R + "id") ?? "");
            bool isLink = relationship is { External: false } && XlsxNames.SameType(relationship.Type, XlsxNames.ExternalLinkRelationship);
            book.ExternalBooks.Add(new ExternalBook(book, isLink ? package.GetPart(relationship!.TargetName) : null));
        }
    }

    /// <summary>The calculation settings of <c>calcPr</c> (ECMA-376 Part 1, 18.2.2); values out of range keep their defaults.</summary>
    private static void ReadCalculation(CalculationSettings settings, XElement? calcPr)
    {
        if (calcPr is null)
        {
            return;
        }

        settings.Mode = (string?)calcPr.Attribute("calcMode") switch
        {
            "manual" => CalculationMode.Manual,
            "autoNoTable" => CalculationMode.AutomaticExceptTables,
            _ => CalculationMode.Automatic,
        };
        settings.Iterate = XlsxNames.Bool((string?)calcPr.Attribute("iterate"), false);
        settings.MaxIterations = Math.Clamp(XlsxNames.Int((string?)calcPr.Attribute("iterateCount"), 100), 1, CalculationSettings.IterationLimit);
        if (double.TryParse((string?)calcPr.Attribute("iterateDelta"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double delta)
            && double.IsFinite(delta) && delta >= 0)
        {
            settings.MaxChange = delta;
        }

        settings.FullPrecision = XlsxNames.Bool((string?)calcPr.Attribute("fullPrecision"), true);
        settings.Concurrent = XlsxNames.Bool((string?)calcPr.Attribute("concurrentCalc"), true);
    }

    private static Worksheet AddSheet(Workbook book, string? name)
    {
        try
        {
            return book.Worksheets.Add(string.IsNullOrEmpty(name) ? null : name);
        }
        catch (ArgumentException e)
        {
            throw new DocumentException($"The workbook has a sheet name Excel does not allow: {e.Message}", e);
        }
    }

    private static void ReadName(Workbook book, XElement element, List<(Worksheet? Sheet, int Kept)> positions)
    {
        string? name = (string?)element.Attribute("name");
        string formula = element.Value;
        if (string.IsNullOrEmpty(name) || formula.Length == 0)
        {
            return;
        }

        Worksheet? sheet = null;
        int kept = -1;
        if ((string?)element.Attribute("localSheetId") is { } local)
        {
            int position = XlsxNames.Int(local, -1);
            if ((uint)position >= (uint)positions.Count)
            {
                return;
            }

            (sheet, kept) = positions[position];
        }

        XAttribute[] extra = [.. element.Attributes().Where(a => a.Name.LocalName is not ("name" or "localSheetId" or "hidden" or "comment") || a.Name.Namespace != XNamespace.None)];
        book.Names.AddRead(new DefinedName(name, formula, sheet)
        {
            Hidden = XlsxNames.Bool((string?)element.Attribute("hidden"), false),
            Comment = (string?)element.Attribute("comment"),
            Extra = extra.Length == 0 ? null : extra,
            KeptScope = kept,
        });
    }

    private static XmlReader OpenXml(OpcPart part, OpcPackage package)
    {
        // A file in strict SpreadsheetML is read through the package's namespace mapping; a transitional one is streamed.
        XmlReader reader = SafeXml.Open(part.Data, package.Limits);
        reader.MoveToContent();
        if (reader.NamespaceURI == XlsxNames.StrictMainUri)
        {
            reader.Dispose();
            reader = part.LoadXml().CreateReader();
            reader.MoveToContent();
        }

        return reader;
    }

    private static void ReadSharedStrings(SharedStringTable table, OpcPart part, OpcPackage package)
    {
        try
        {
            using XmlReader reader = OpenXml(part, package);
            if (reader.IsEmptyElement)
            {
                return;
            }

            reader.Read();
            while (!reader.EOF && reader.Depth > 0)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && reader.LocalName == "si")
                {
                    XElement si = (XElement)XNode.ReadFrom(reader);
                    XNamespace ns = si.Name.Namespace;
                    if (!si.HasAttributes && si.Elements().Count() == 1 && si.Element(ns + "t") is { } plain)
                    {
                        table.AddRead(XlsxNames.Decode(plain.Value), null);
                    }
                    else
                    {
                        string text = string.Concat(si.Elements(ns + "t").Concat(si.Elements(ns + "r").Elements(ns + "t")).Select(t => t.Value));
                        table.AddRead(XlsxNames.Decode(text), si);
                    }
                }
                else
                {
                    reader.Read();
                }
            }
        }
        catch (XmlException e)
        {
            throw new DocumentException($"Part {part.Name} is not well-formed XML: {e.Message}", e);
        }
    }

    private static void ReadWorksheet(Workbook book, Worksheet sheet, OpcPart part, OpcPackage package, WorkbookSource workbookSource)
    {
        SheetSource source = sheet.Source!;
        try
        {
            using XmlReader reader = OpenXml(part, package);
            if (reader.LocalName != "worksheet")
            {
                throw new DocumentException($"Part {part.Name} is not a worksheet.");
            }

            if (reader.MoveToFirstAttribute())
            {
                do
                {
                    XName name = reader.Prefix.Length == 0 && reader.LocalName == "xmlns" ? "xmlns" : XNamespace.Get(reader.NamespaceURI) + reader.LocalName;
                    source.RootAttributes.Add(new XAttribute(name, reader.Value));
                }
                while (reader.MoveToNextAttribute());

                reader.MoveToElement();
            }

            if (reader.IsEmptyElement)
            {
                return;
            }

            reader.Read();
            double rank = -1;
            while (!reader.EOF && reader.Depth > 0)
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1)
                {
                    reader.Read();
                    continue;
                }

                if (reader.LocalName == "sheetData" && XlsxNames.IsMain(reader.NamespaceURI))
                {
                    rank = WorksheetOrder.Rank("sheetData");
                    SheetDataReader.Read(book, sheet, reader, workbookSource);
                    continue;
                }

                XElement element = (XElement)XNode.ReadFrom(reader);
                int known = WorksheetOrder.Rank(element.Name);
                if (known >= 0)
                {
                    rank = known;
                }

                if (!ReadModeled(book, sheet, element))
                {
                    source.Kept.Add((known >= 0 ? known : rank + 0.5, element));
                }
            }
        }
        catch (XmlException e)
        {
            throw new DocumentException($"Part {part.Name} is not well-formed XML: {e.Message}", e);
        }

        SheetObjectReader.ReadComments(sheet);
    }

    /// <summary>Reads an element the model holds; false for one it keeps as it is.</summary>
    private static bool ReadModeled(Workbook book, Worksheet sheet, XElement element)
    {
        if (WorksheetOrder.Rank(element.Name) < 0)
        {
            return false;
        }

        XNamespace ns = element.Name.Namespace;
        SheetSource source = sheet.Source!;
        PageSetup page = sheet.PageSetup;
        switch (element.Name.LocalName)
        {
            case "sheetPr":
                source.SheetProperties = element;
                if (element.Element(ns + "tabColor") is { } tab)
                {
                    sheet.TabColor = XlsxStyles.ReadColor(tab).Color;
                }

                page.FitToPage = XlsxNames.Bool((string?)element.Element(ns + "pageSetUpPr")?.Attribute("fitToPage"), false);
                return true;
            case "dimension":
                return true;
            case "sheetViews":
                source.SheetViews = element;
                ReadView(sheet.View, element.Element(ns + "sheetView"));
                return true;
            case "sheetFormatPr":
                source.SheetFormat = element;
                if (XlsxNames.TryNumber((string?)element.Attribute("defaultRowHeight"), out double height) && height is >= 0 and <= 409)
                {
                    sheet.DefaultRowHeight = height;
                    sheet.DefaultRowFollowsFont = !XlsxNames.Bool((string?)element.Attribute("customHeight"), false);
                }

                if (XlsxNames.TryNumber((string?)element.Attribute("defaultColWidth"), out double width) && width is >= 0 and <= 255)
                {
                    sheet.DefaultColumnWidth = width;
                }

                return true;
            case "cols":
                foreach (XElement col in element.Elements(ns + "col"))
                {
                    ReadColumn(book, sheet, col);
                }

                return true;
            case "mergeCells":
                foreach (XElement merge in element.Elements(ns + "mergeCell"))
                {
                    if (RangeAddress.TryParse((string?)merge.Attribute("ref"), out RangeAddress range))
                    {
                        sheet.MergedCells.AddRead(range);
                    }
                }

                return true;
            case "printOptions":
                page.SourcePrintOptions = element;
                page.CenterHorizontally = XlsxNames.Bool((string?)element.Attribute("horizontalCentered"), false);
                page.CenterVertically = XlsxNames.Bool((string?)element.Attribute("verticalCentered"), false);
                page.PrintHeadings = XlsxNames.Bool((string?)element.Attribute("headings"), false);
                page.PrintGridLines = XlsxNames.Bool((string?)element.Attribute("gridLines"), false);
                return true;
            case "pageMargins":
                page.SourceMargins = element;
                page.LeftMargin = Margin(element, "left", page.LeftMargin);
                page.RightMargin = Margin(element, "right", page.RightMargin);
                page.TopMargin = Margin(element, "top", page.TopMargin);
                page.BottomMargin = Margin(element, "bottom", page.BottomMargin);
                page.HeaderMargin = Margin(element, "header", page.HeaderMargin);
                page.FooterMargin = Margin(element, "footer", page.FooterMargin);
                return true;
            case "pageSetup":
                ReadPageSetup(page, element);
                return true;
            case "rowBreaks":
                page.SourceRowBreaks = element;
                ReadBreaks(page.RowBreaks, element, CellAddress.MaxRows);
                return true;
            case "colBreaks":
                page.SourceColumnBreaks = element;
                ReadBreaks(page.ColumnBreaks, element, CellAddress.MaxColumns);
                return true;
            case "autoFilter":
                sheet.HasFilterCriteria = element.Element(ns + "filterColumn") is not null;
                return false;
            case "headerFooter":
                page.SourceHeaderFooter = element;
                page.Header = (string?)element.Element(ns + "oddHeader");
                page.Footer = (string?)element.Element(ns + "oddFooter");
                return true;
            default:
                return SheetObjectReader.Read(book, sheet, element);
        }
    }

    /// <summary>The manual breaks of rowBreaks or colBreaks: each brk's id is the row or column the break comes before.</summary>
    private static void ReadBreaks(PageBreakCollection breaks, XElement element, int limit)
    {
        foreach (XElement brk in element.Elements(element.Name.Namespace + "brk"))
        {
            int id = XlsxNames.Int((string?)brk.Attribute("id"), 0);
            if (XlsxNames.Bool((string?)brk.Attribute("man"), false) && id > 0 && id < limit)
            {
                breaks.Add(id);
            }
        }
    }

    private static double Margin(XElement element, string name, double fallback) =>
        XlsxNames.TryNumber((string?)element.Attribute(name), out double value) && value >= 0 ? value : fallback;

    private static void ReadView(SheetView view, XElement? element)
    {
        if (element is null)
        {
            return;
        }

        view.ShowGridLines = XlsxNames.Bool((string?)element.Attribute("showGridLines"), true);
        view.ShowHeadings = XlsxNames.Bool((string?)element.Attribute("showRowColHeaders"), true);
        view.ShowZeros = XlsxNames.Bool((string?)element.Attribute("showZeros"), true);
        view.ShowFormulas = XlsxNames.Bool((string?)element.Attribute("showFormulas"), false);
        view.RightToLeft = XlsxNames.Bool((string?)element.Attribute("rightToLeft"), false);
        view.Selected = XlsxNames.Bool((string?)element.Attribute("tabSelected"), false);
        int zoom = XlsxNames.Int((string?)element.Attribute("zoomScale"), 100);
        view.ZoomScale = zoom is >= 10 and <= 400 ? zoom : 100;
        if (element.Element(element.Name.Namespace + "pane") is { } pane && (string?)pane.Attribute("state") is "frozen" or "frozenSplit")
        {
            int rows = XlsxNames.TryNumber((string?)pane.Attribute("ySplit"), out double y) ? (int)y : 0;
            int columns = XlsxNames.TryNumber((string?)pane.Attribute("xSplit"), out double x) ? (int)x : 0;
            view.FrozenRows = Math.Clamp(rows, 0, CellAddress.MaxRows - 1);
            view.FrozenColumns = Math.Clamp(columns, 0, CellAddress.MaxColumns - 1);
        }

        view.SourceFrozen = (view.FrozenRows, view.FrozenColumns);
    }

    private static void ReadColumn(Workbook book, Worksheet sheet, XElement col)
    {
        int first = XlsxNames.Int((string?)col.Attribute("min"), 0);
        int last = XlsxNames.Int((string?)col.Attribute("max"), first);
        if (first < 1 || last < first)
        {
            return;
        }

        last = Math.Min(last, CellAddress.MaxColumns);
        if (first > last)
        {
            return;
        }

        int style = XlsxNames.Int((string?)col.Attribute("style"), -1);
        int outline = XlsxNames.Int((string?)col.Attribute("outlineLevel"), 0);
        XAttribute[] extra = [.. col.Attributes().Where(a => a.Name.Namespace != XNamespace.None
            || a.Name.LocalName is not ("min" or "max" or "width" or "hidden" or "outlineLevel" or "collapsed" or "style"))];
        sheet.ColumnSpans.AddRead(new ColumnSpan
        {
            First = first - 1,
            Last = last - 1,
            Width = XlsxNames.TryNumber((string?)col.Attribute("width"), out double width) && width is >= 0 and <= 255 ? width : double.NaN,
            Hidden = XlsxNames.Bool((string?)col.Attribute("hidden"), false),
            OutlineLevel = Math.Clamp(outline, 0, 7),
            Collapsed = XlsxNames.Bool((string?)col.Attribute("collapsed"), false),
            Style = style > 0 && style < book.Styles.Count ? style : -1,
            Extra = extra.Length == 0 ? null : extra,
        });
    }

    private static void ReadPageSetup(PageSetup page, XElement element)
    {
        page.SourcePageSetup = element;
        // The attribute's default is Letter, while a sheet with no page setup, or paper 0, prints on the printer's paper.

        // No paperSize is Letter by the standard, but a sheet fitted to pages fits the printer's paper, as Excel's PDFs measure.
        int paper = XlsxNames.Int((string?)element.Attribute("paperSize"), page.FitToPage ? 0 : 1);
        page.PaperSize = paper == 0 ? null : paper;

        int scale = XlsxNames.Int((string?)element.Attribute("scale"), 100);
        page.Scale = scale is >= 10 and <= 400 && element.Attribute("scale") is not null ? scale : null;
        page.FitToWidth = Math.Max(0, XlsxNames.Int((string?)element.Attribute("fitToWidth"), 1));
        page.FitToHeight = Math.Max(0, XlsxNames.Int((string?)element.Attribute("fitToHeight"), 1));
        page.Orientation = (string?)element.Attribute("orientation") switch
        {
            "portrait" => PrintOrientation.Portrait,
            "landscape" => PrintOrientation.Landscape,
            _ => PrintOrientation.Default,
        };
        page.PageOrder = (string?)element.Attribute("pageOrder") == "overThenDown" ? PrintPageOrder.OverThenDown : PrintPageOrder.DownThenOver;
        if (XlsxNames.Bool((string?)element.Attribute("useFirstPageNumber"), false) && (string?)element.Attribute("firstPageNumber") is { } number)
        {
            page.FirstPageNumber = XlsxNames.Int(number, 1);
        }
    }
}

/// <summary>Streams a worksheet's sheetData element into the sheet's cell store (ECMA-376 Part 1, 18.3.1.80, 18.3.1.73, 18.3.1.4).</summary>
internal static class SheetDataReader
{
    private static readonly DateTime Epoch1900 = new(1899, 12, 30, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Epoch1904 = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    public static void Read(Workbook book, Worksheet sheet, XmlReader reader, WorkbookSource source)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return;
        }

        int depth = reader.Depth;
        int nextRow = 0;
        reader.Read();
        while (!reader.EOF && reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
            {
                nextRow = ReadRow(book, sheet, reader, nextRow, source) + 1;
            }
            else
            {
                reader.Read();
            }
        }

        if (reader.NodeType == XmlNodeType.EndElement)
        {
            reader.Read();
        }
    }

    private static int ReadRow(Workbook book, Worksheet sheet, XmlReader reader, int row, WorkbookSource source)
    {
        RowRecord record = new();
        List<XAttribute>? extra = null;
        string? height = null;
        bool customHeight = false;
        int style = -1;
        bool customFormat = false;
        while (reader.MoveToNextAttribute())
        {
            if (reader.NamespaceURI.Length != 0)
            {
                if (reader.NamespaceURI != "http://www.w3.org/2000/xmlns/")
                {
                    (extra ??= []).Add(new XAttribute(XNamespace.Get(reader.NamespaceURI) + reader.LocalName, reader.Value));
                }

                continue;
            }

            switch (reader.LocalName)
            {
                case "r":
                    if (int.TryParse(reader.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int r) && r >= 1 && r <= CellAddress.MaxRows)
                    {
                        row = r - 1;
                    }

                    break;
                case "spans":
                    break;
                case "ht":
                    height = reader.Value;
                    break;
                case "customHeight":
                    customHeight = XlsxNames.Bool(reader.Value, false);
                    break;
                case "hidden":
                    record.Hidden = XlsxNames.Bool(reader.Value, false);
                    break;
                case "outlineLevel":
                    record.OutlineLevel = Math.Clamp(XlsxNames.Int(reader.Value, 0), 0, 7);
                    break;
                case "collapsed":
                    record.Collapsed = XlsxNames.Bool(reader.Value, false);
                    break;
                case "s":
                    style = XlsxNames.Int(reader.Value, -1);
                    break;
                case "customFormat":
                    customFormat = XlsxNames.Bool(reader.Value, false);
                    break;
                default:
                    (extra ??= []).Add(new XAttribute(reader.LocalName, reader.Value));
                    break;
            }
        }

        reader.MoveToElement();
        if (height is not null)
        {
            if (customHeight && XlsxNames.TryNumber(height, out double points) && points is >= 0 and <= 409)
            {
                record.Height = points;
            }
            else
            {
                // A height Excel worked out from the row's text: kept as it was, for Excel to work out again.
                (extra ??= []).Add(new XAttribute("ht", height));
                if (!customHeight && XlsxNames.TryNumber(height, out double fitted) && fitted is >= 0 and <= 409)
                {
                    record.FittedHeight = fitted;
                }

                if (customHeight)
                {
                    extra.Add(new XAttribute("customHeight", "1"));
                }
            }
        }

        if (customFormat && style > 0 && style < book.Styles.Count)
        {
            record.Style = style;
        }

        record.Extra = extra?.ToArray();
        if (!record.IsDefault)
        {
            sheet.RowRecords[row] = record;
        }

        if (reader.IsEmptyElement)
        {
            reader.Read();
            return row;
        }

        int depth = reader.Depth;
        int nextColumn = 0;
        reader.Read();
        while (!reader.EOF && reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
            {
                nextColumn = ReadCell(book, sheet, reader, row, nextColumn, source) + 1;
            }
            else
            {
                reader.Read();
            }
        }

        if (reader.NodeType == XmlNodeType.EndElement)
        {
            reader.Read();
        }

        return row;
    }

    private static int ReadCell(Workbook book, Worksheet sheet, XmlReader reader, int row, int column, WorkbookSource source)
    {
        string? type = null;
        int style = 0;
        List<XObject>? extra = null;
        while (reader.MoveToNextAttribute())
        {
            if (reader.NamespaceURI.Length != 0)
            {
                if (reader.NamespaceURI != "http://www.w3.org/2000/xmlns/")
                {
                    (extra ??= []).Add(new XAttribute(XNamespace.Get(reader.NamespaceURI) + reader.LocalName, reader.Value));
                }

                continue;
            }

            switch (reader.LocalName)
            {
                case "r":
                    if (CellAddress.TryParse(reader.Value, out CellAddress address))
                    {
                        row = address.Row;
                        column = address.Column;
                    }

                    break;
                case "t":
                    type = reader.Value;
                    break;
                case "s":
                    style = XlsxNames.Int(reader.Value, 0);
                    break;
                default:
                    (extra ??= []).Add(new XAttribute(reader.LocalName, reader.Value));
                    break;
            }
        }

        reader.MoveToElement();
        string? value = null;
        string? formula = null;
        XAttribute[]? formulaAttributes = null;
        string? inline = null;
        if (reader.IsEmptyElement)
        {
            reader.Read();
        }
        else
        {
            int depth = reader.Depth;
            reader.Read();
            while (!reader.EOF && reader.Depth > depth)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "v" when XlsxNames.IsMain(reader.NamespaceURI):
                        value = reader.ReadElementContentAsString();
                        break;
                    case "f" when XlsxNames.IsMain(reader.NamespaceURI):
                        List<XAttribute> attributes = [];
                        while (reader.MoveToNextAttribute())
                        {
                            attributes.Add(new XAttribute(XNamespace.Get(reader.NamespaceURI) + reader.LocalName, reader.Value));
                        }

                        reader.MoveToElement();
                        formulaAttributes = attributes.Count == 0 ? null : [.. attributes];
                        formula = reader.ReadElementContentAsString();
                        break;
                    case "is" when XlsxNames.IsMain(reader.NamespaceURI):
                        XElement text = (XElement)XNode.ReadFrom(reader);
                        XNamespace ns = text.Name.Namespace;
                        inline = XlsxNames.Decode(string.Concat(text.Elements(ns + "t").Concat(text.Elements(ns + "r").Elements(ns + "t")).Select(t => t.Value)));
                        break;
                    default:
                        (extra ??= []).Add(XNode.ReadFrom(reader));
                        break;
                }
            }

            if (reader.NodeType == XmlNodeType.EndElement)
            {
                reader.Read();
            }
        }

        CellRecord record = new() { Style = (uint)style < (uint)book.Styles.Count ? style : 0 };
        CellValue result = Value(book, type, value, inline, formula is not null, ref record);
        FormulaEntry? entry = null;
        if (formula is not null)
        {
            source.HadFormulas = true;
            entry = new FormulaEntry(formula, result) { Attributes = formulaAttributes };
            record = new CellRecord { Style = record.Style };
        }

        if (entry is null && record.IsVacant && extra is null)
        {
            return column;
        }

        sheet.SetRead(row, column, record, entry);
        if (extra is not null)
        {
            sheet.CellExtras[Worksheet.Key(row, column)] = new XElement(XlsxNames.Main + "c", extra);
        }

        return column;
    }

    /// <summary>The cell's value; for a value that is not a formula's result, also stored in <paramref name="record"/>.</summary>
    private static CellValue Value(Workbook book, string? type, string? value, string? inline, bool formula, ref CellRecord record)
    {
        switch (type)
        {
            case "s":
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < book.SharedStrings.Count)
                {
                    record.Kind = CellKind.SharedString;
                    record.Payload = index;
                    return CellValue.TextUnchecked(book.SharedStrings[index]);
                }

                return CellValue.Empty;
            case "inlineStr":
                return inline is null ? CellValue.Empty : Text(book, inline, formula, ref record);
            case "str":
                return value is null ? CellValue.Empty : Text(book, XlsxNames.Decode(value), formula, ref record);
            case "b":
                if (value is null)
                {
                    return CellValue.Empty;
                }

                record.Kind = CellKind.Boolean;
                record.Payload = XlsxNames.Bool(value.Trim(), false) ? 1 : 0;
                return CellValue.FromBoolean(record.Payload != 0);
            case "e":
                if (value is not null && CellValue.TryParseError(value.Trim(), out CellError error))
                {
                    record.Kind = CellKind.Error;
                    record.Payload = (long)error;
                    return CellValue.FromError(error);
                }

                return CellValue.Empty;
            case "d":
                if (value is not null && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out DateTime date))
                {
                    double serial = (date - (book.Date1904 ? Epoch1904 : Epoch1900)).TotalDays;
                    return Number(serial, ref record);
                }

                return CellValue.Empty;
            default:
                return XlsxNames.TryNumber(value, out double number) ? Number(number, ref record) : CellValue.Empty;
        }
    }

    private static CellValue Number(double number, ref CellRecord record)
    {
        number = number == 0 ? 0 : number;
        record.Kind = CellKind.Number;
        record.Payload = BitConverter.DoubleToInt64Bits(number);
        return CellValue.NumberUnchecked(number);
    }

    private static CellValue Text(Workbook book, string text, bool formula, ref CellRecord record)
    {
        if (text.Length > CellValue.MaxTextLength)
        {
            text = text[..CellValue.MaxTextLength];
        }

        if (formula)
        {
            // A formula's text result is kept with the formula, not in the shared strings.
            return CellValue.TextUnchecked(text);
        }

        record.Kind = CellKind.SharedString;
        record.Payload = book.SharedStrings.Add(text);
        return CellValue.TextUnchecked(text);
    }
}
