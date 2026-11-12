using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Docx;

namespace Egap.Spreadsheets.Xlsx;

/// <summary>
/// Reads what a worksheet holds besides its cells into the model: hyperlinks, data validation, conditional formatting,
/// tables, notes and the drawing layer (ECMA-376 Part 1, 18.3.1, 18.5, 18.7 and 20.5). Each keeps the file's element so a
/// save writes back what the model does not hold.
/// </summary>
internal static class SheetObjectReader
{
    public const string HyperlinkRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink";
    public const string TableRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/table";
    public const string DrawingRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing";
    public const string ImageRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";
    public const string CommentsRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments";
    public const string VmlRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/vmlDrawing";

    public static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    public static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    public static readonly XNamespace Cx = "http://schemas.microsoft.com/office/drawing/2014/chartex";
    public static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    /// <summary>Reads an element the model holds; false for one kept as it is.</summary>
    public static bool Read(Workbook book, Worksheet sheet, XElement element)
    {
        XNamespace ns = element.Name.Namespace;
        SheetSource source = sheet.Source!;
        switch (element.Name.LocalName)
        {
            case "hyperlinks":
                source.Hyperlinks = new XElement(element.Name, element.Attributes());
                foreach (XElement link in element.Elements(ns + "hyperlink"))
                {
                    ReadHyperlink(sheet, link);
                }

                return true;
            case "dataValidations":
                sheet.DataValidations.Source = new XElement(element.Name, element.Attributes());
                foreach (XElement rule in element.Elements(ns + "dataValidation"))
                {
                    ReadValidation(sheet, rule);
                }

                return true;
            case "conditionalFormatting":
                ReadConditionalFormat(book, sheet, element);
                return true;
            case "tableParts":
                source.TableParts = new XElement(element.Name, element.Attributes());
                foreach (XElement part in element.Elements(ns + "tablePart"))
                {
                    ReadTable(sheet, (string?)part.Attribute(XlsxNames.R + "id"));
                }

                return true;
            case "drawing":
                return ReadDrawing(sheet, element);
            case "legacyDrawing":
                source.LegacyDrawing = element;
                source.VmlPart = Related(sheet, (string?)element.Attribute(XlsxNames.R + "id"));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads the sheet's notes from its comments part, found by relationship type as the sheet names it nowhere else.</summary>
    public static void ReadComments(Worksheet sheet)
    {
        SheetSource source = sheet.Source!;
        OpcPart? part = Package(sheet).GetRelatedPart(source.Part.Relationships, CommentsRelationship);
        if (part is null)
        {
            return;
        }

        source.CommentsPart = part;
        XElement? root = part.LoadXml().Root;
        if (root is null)
        {
            return;
        }

        XNamespace ns = root.Name.Namespace;
        string[] authors = [.. root.Element(ns + "authors")?.Elements(ns + "author").Select(a => a.Value) ?? []];
        HashSet<CellAddress> visible = VisibleNotes(source.VmlPart);
        foreach (XElement comment in root.Element(ns + "commentList")?.Elements(ns + "comment") ?? [])
        {
            if (!CellAddress.TryParse((string?)comment.Attribute("ref"), out CellAddress cell) || sheet.Comments[cell] is not null)
            {
                continue;
            }

            int author = XlsxNames.Int((string?)comment.Attribute("authorId"), -1);
            XElement? text = comment.Element(ns + "text");
            string plain = text is null ? "" : XlsxNames.Decode(string.Concat(text.Elements(ns + "t").Concat(text.Elements(ns + "r").Elements(ns + "t")).Select(t => t.Value)));
            CellComment read = new(cell, plain, (uint)author < (uint)authors.Length ? authors[author] : "")
            {
                Visible = visible.Contains(cell),
            };
            read.RichText = text;
            read.ReadState = SheetObjectWriter.State(read);
            sheet.Comments.AddRead(read);
        }
    }

    /// <summary>
    /// Where the sheet's pivot tables are, from their parts' locations, or where a refresh put them; a part that cannot be
    /// read gives no range.
    /// </summary>
    public static List<RangeAddress> PivotTables(Worksheet sheet) => [.. sheet.PivotTables.Select(t => t.Range)];

    private static OpcPackage Package(Worksheet sheet) => sheet.Workbook.Source!.Package;

    private static OpcPart? Related(Worksheet sheet, string? id)
    {
        OpcRelationship? relationship = id is null ? null : sheet.Source!.Part.GetRelationship(id);
        return relationship is { External: false } ? Package(sheet).GetPart(relationship.TargetName) : null;
    }

    private static List<RangeAddress> Ranges(string? sqref)
    {
        List<RangeAddress> ranges = [];
        foreach (string part in (sqref ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (RangeAddress.TryParse(part, out RangeAddress range))
            {
                ranges.Add(range);
            }
        }

        return ranges;
    }

    private static void ReadHyperlink(Worksheet sheet, XElement element)
    {
        if (!RangeAddress.TryParse((string?)element.Attribute("ref"), out RangeAddress range))
        {
            return;
        }

        string? address = null;
        (string, string)? relationship = null;
        if ((string?)element.Attribute(XlsxNames.R + "id") is { } id && sheet.Source!.Part.GetRelationship(id) is { } rel)
        {
            address = rel.Target;
            relationship = (id, rel.Target);
        }

        // location, display and tooltip are ST_Xstring (22.9.2.19): characters XML cannot hold come as _xHHHH_ escapes.
        string? location = Decoded(element.Attribute("location"));
        if (address is null && location is null)
        {
            return;
        }

        sheet.Hyperlinks.AddRead(new Hyperlink(range, address, location)
        {
            Display = Decoded(element.Attribute("display")),
            ToolTip = Decoded(element.Attribute("tooltip")),
            Source = element,
            Relationship = relationship,
        });
    }

    private static string? Decoded(XAttribute? attribute) => attribute is null ? null : XlsxNames.Decode(attribute.Value);

    internal static void ReadValidation(Worksheet sheet, XElement element)
    {
        XNamespace ns = element.Name.Namespace;
        List<RangeAddress> ranges = Ranges((string?)element.Attribute("sqref"));
        if (ranges.Count == 0)
        {
            return;
        }

        DataValidationType type = ValidationType((string?)element.Attribute("type"));
        sheet.DataValidations.AddRead(new DataValidation(ranges, type)
        {
            Operator = Operator((string?)element.Attribute("operator")),
            Formula1 = (string?)element.Element(ns + "formula1"),
            Formula2 = (string?)element.Element(ns + "formula2"),
            AllowBlank = XlsxNames.Bool((string?)element.Attribute("allowBlank"), false),
            ShowDropDown = !XlsxNames.Bool((string?)element.Attribute("showDropDown"), false),
            ShowInputMessage = XlsxNames.Bool((string?)element.Attribute("showInputMessage"), false),
            ShowErrorMessage = XlsxNames.Bool((string?)element.Attribute("showErrorMessage"), false),
            ErrorStyle = ErrorStyle((string?)element.Attribute("errorStyle")),
            PromptTitle = (string?)element.Attribute("promptTitle"),
            Prompt = (string?)element.Attribute("prompt"),
            ErrorTitle = (string?)element.Attribute("errorTitle"),
            Error = (string?)element.Attribute("error"),
            Source = element,
        });
    }

    // The enum values an attribute reads as. A value the model does not know reads as the default; the writer keeps the
    // file's attribute while the model's value is still the one read, so such values are written back as they were.

    /// <summary>A comparison operator (ST_DataValidationOperator, ST_ConditionalFormattingOperator); between for others.</summary>
    internal static ComparisonOperator Operator(string? value) => value switch
    {
        "notBetween" => ComparisonOperator.NotBetween,
        "equal" => ComparisonOperator.Equal,
        "notEqual" => ComparisonOperator.NotEqual,
        "lessThan" => ComparisonOperator.LessThan,
        "lessThanOrEqual" => ComparisonOperator.LessThanOrEqual,
        "greaterThan" => ComparisonOperator.GreaterThan,
        "greaterThanOrEqual" => ComparisonOperator.GreaterThanOrEqual,
        _ => ComparisonOperator.Between,
    };

    internal static DataValidationType ValidationType(string? value) => value switch
    {
        "whole" => DataValidationType.WholeNumber,
        "decimal" => DataValidationType.DecimalNumber,
        "list" => DataValidationType.List,
        "date" => DataValidationType.Date,
        "time" => DataValidationType.Time,
        "textLength" => DataValidationType.TextLength,
        "custom" => DataValidationType.Custom,
        _ => DataValidationType.Any,
    };

    internal static DataValidationErrorStyle ErrorStyle(string? value) => value switch
    {
        "warning" => DataValidationErrorStyle.Warning,
        "information" => DataValidationErrorStyle.Information,
        _ => DataValidationErrorStyle.Stop,
    };

    internal static TableTotalsFunction TotalsFunction(string? value) => value switch
    {
        "sum" => TableTotalsFunction.Sum,
        "min" => TableTotalsFunction.Min,
        "max" => TableTotalsFunction.Max,
        "average" => TableTotalsFunction.Average,
        "count" => TableTotalsFunction.Count,
        "countNums" => TableTotalsFunction.CountNumbers,
        "stdDev" => TableTotalsFunction.StandardDeviation,
        "var" => TableTotalsFunction.Variance,
        "custom" => TableTotalsFunction.Custom,
        _ => TableTotalsFunction.None,
    };

    internal static readonly (ConditionalFormatType Type, string Name)[] RuleTypes =
    [
        (ConditionalFormatType.Expression, "expression"), (ConditionalFormatType.CellIs, "cellIs"),
        (ConditionalFormatType.ColorScale, "colorScale"), (ConditionalFormatType.DataBar, "dataBar"),
        (ConditionalFormatType.IconSet, "iconSet"), (ConditionalFormatType.Top10, "top10"),
        (ConditionalFormatType.UniqueValues, "uniqueValues"), (ConditionalFormatType.DuplicateValues, "duplicateValues"),
        (ConditionalFormatType.ContainsText, "containsText"), (ConditionalFormatType.NotContainsText, "notContainsText"),
        (ConditionalFormatType.BeginsWith, "beginsWith"), (ConditionalFormatType.EndsWith, "endsWith"),
        (ConditionalFormatType.ContainsBlanks, "containsBlanks"), (ConditionalFormatType.NotContainsBlanks, "notContainsBlanks"),
        (ConditionalFormatType.ContainsErrors, "containsErrors"), (ConditionalFormatType.NotContainsErrors, "notContainsErrors"),
        (ConditionalFormatType.TimePeriod, "timePeriod"), (ConditionalFormatType.AboveAverage, "aboveAverage"),
    ];

    internal static void ReadConditionalFormat(Workbook book, Worksheet sheet, XElement element)
    {
        XNamespace ns = element.Name.Namespace;
        ConditionalFormat format = new(Ranges((string?)element.Attribute("sqref"))) { Source = element };
        foreach (XElement rule in element.Elements(ns + "cfRule"))
        {
            string type = (string?)rule.Attribute("type") ?? "";
            int found = Array.FindIndex(RuleTypes, t => t.Name == type);

            // A type the model does not know reads as an expression; the writer keeps the file's type while the rule has one.
            ConditionalFormatRule read = new(found < 0 ? ConditionalFormatType.Expression : RuleTypes[found].Type)
            {
                Operator = Operator((string?)rule.Attribute("operator")),
                Text = (string?)rule.Attribute("text"),
                Priority = XlsxNames.Int((string?)rule.Attribute("priority"), 0),
                StopIfTrue = XlsxNames.Bool((string?)rule.Attribute("stopIfTrue"), false),
                Source = rule,
            };
            read.Formulas.AddRange(rule.Elements(ns + "formula").Select(f => f.Value));
            if ((string?)rule.Attribute("dxfId") is { } dxf && XlsxNames.Int(dxf, -1) is int index and >= 0)
            {
                CellStyle? style = index < book.Styles.DifferentialStyles.Count ? book.Styles.DifferentialStyles[index] : null;
                read.Style = style;
                read.ReadStyle = (index, style);
                read.StatedFontFlags = index < book.Styles.DifferentialFontFlags.Count ? book.Styles.DifferentialFontFlags[index] : 0;
            }

            format.Rules.Add(read);
        }

        if (format.Ranges.Count > 0 && format.Rules.Count > 0)
        {
            sheet.ConditionalFormats.AddRead(format);
        }
    }

    private static void ReadTable(Worksheet sheet, string? id)
    {
        if (Related(sheet, id) is not { } part || part.LoadXml().Root is not { } root || !RangeAddress.TryParse((string?)root.Attribute("ref"), out RangeAddress range))
        {
            return;
        }

        XNamespace ns = root.Name.Namespace;
        string name = (string?)root.Attribute("displayName") ?? (string?)root.Attribute("name") ?? "";
        if (name.Length == 0 || sheet.Workbook.Worksheets.SelectMany(s => s.Tables).Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        XElement? style = root.Element(ns + "tableStyleInfo");
        SheetTable table = new(sheet, name, range)
        {
            ShowHeaderRow = XlsxNames.Int((string?)root.Attribute("headerRowCount"), 1) != 0,
            ShowTotalsRow = XlsxNames.Int((string?)root.Attribute("totalsRowCount"), 0) != 0,
            ShowFilterButtons = root.Element(ns + "autoFilter") is not null,
            StyleName = (string?)style?.Attribute("name"),
            ShowRowStripes = XlsxNames.Bool((string?)style?.Attribute("showRowStripes"), false),
            ShowColumnStripes = XlsxNames.Bool((string?)style?.Attribute("showColumnStripes"), false),
            ShowFirstColumn = XlsxNames.Bool((string?)style?.Attribute("showFirstColumn"), false),
            ShowLastColumn = XlsxNames.Bool((string?)style?.Attribute("showLastColumn"), false),
            Source = (part.Name, root, [.. part.Relationships]),
            RelationshipId = id,
        };
        foreach (XElement column in root.Element(ns + "tableColumns")?.Elements(ns + "tableColumn") ?? [])
        {
            table.Columns.Add(new SheetTableColumn(XlsxNames.Decode((string?)column.Attribute("name") ?? ""))
            {
                TotalsRowFunction = TotalsFunction((string?)column.Attribute("totalsRowFunction")),
                TotalsRowLabel = (string?)column.Attribute("totalsRowLabel"),
                TotalsRowFormula = (string?)column.Element(ns + "totalsRowFormula"),
                CalculatedFormula = (string?)column.Element(ns + "calculatedColumnFormula"),
                Source = column,
            });
        }

        table.ReadState = SheetObjectWriter.State(table);
        sheet.Tables.AddRead(table);
    }

    private static bool ReadDrawing(Worksheet sheet, XElement element)
    {
        if (Related(sheet, (string?)element.Attribute(XlsxNames.R + "id")) is not { } part || part.LoadXml().Root is not { } root || root.Name != Xdr + "wsDr")
        {
            return false;
        }

        sheet.Source!.Drawing = (element, part);
        foreach (XElement anchor in root.Elements())
        {
            DrawingAnchorKind? kind = anchor.Name.LocalName switch
            {
                "twoCellAnchor" => DrawingAnchorKind.TwoCell,
                "oneCellAnchor" => DrawingAnchorKind.OneCell,
                "absoluteAnchor" => DrawingAnchorKind.Absolute,
                _ => null,
            };
            if (anchor.Name.Namespace != Xdr || kind is null)
            {
                continue;
            }

            XElement? shape = ObjectOf(anchor);
            XElement? content = shape?.Name == Mc + "AlternateContent" ? shape.Elements().FirstOrDefault()?.Elements().FirstOrDefault() : shape;
            SheetDrawingKind objectKind = content?.Name.LocalName switch
            {
                "pic" => SheetDrawingKind.Picture,
                "sp" => SheetDrawingKind.Shape,
                "grpSp" => SheetDrawingKind.Group,
                "graphicFrame" when ChartReference(content) is not null => SheetDrawingKind.Chart,
                _ => SheetDrawingKind.Other,
            };
            SheetDrawing drawing = new(objectKind)
            {
                AnchorKind = kind.Value,
                Source = anchor,
            };
            if (anchor.Element(Xdr + "from") is { } from)
            {
                drawing.From = Position(from);
            }

            if (anchor.Element(Xdr + "to") is { } to)
            {
                drawing.To = Position(to);
            }

            if (anchor.Element(Xdr + "pos") is { } pos)
            {
                drawing.X = Long(pos.Attribute("x"));
                drawing.Y = Long(pos.Attribute("y"));
            }

            if (anchor.Element(Xdr + "ext") is { } ext)
            {
                drawing.Width = Long(ext.Attribute("cx"));
                drawing.Height = Long(ext.Attribute("cy"));
            }

            if (content?.Descendants(Xdr + "cNvPr").FirstOrDefault() is { } properties)
            {
                drawing.Name = (string?)properties.Attribute("name") ?? "";
                drawing.Description = (string?)properties.Attribute("descr");
            }

            if (objectKind == SheetDrawingKind.Shape && content!.Element(Xdr + "txBody") is { } body)
            {
                drawing.Text = string.Join("\n", body.Elements(A + "p").Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value))));
            }

            if (objectKind == SheetDrawingKind.Picture && content!.Descendants(A + "blip").FirstOrDefault()?.Attribute(XlsxNames.R + "embed")?.Value is { } embed
                && part.GetRelationship(embed) is { External: false } image && Package(sheet).GetPart(image.TargetName) is { } imagePart)
            {
                drawing.ImageRelationshipId = embed;
                drawing.SetImageRead(imagePart.Data, imagePart.ContentType);
            }

            if (objectKind == SheetDrawingKind.Chart && (string?)ChartReference(content!)!.Attribute(XlsxNames.R + "id") is { } chartId
                && part.GetRelationship(chartId) is { External: false } chartRelationship)
            {
                drawing.ChartPart = Package(sheet).GetPart(chartRelationship.TargetName);
            }

            drawing.ReadState = SheetObjectWriter.State(drawing);
            sheet.Drawings.AddRead(drawing);
        }

        return true;
    }

    internal static XElement? ChartReference(XElement frame) => frame.Descendants().FirstOrDefault(e => e.Name == C + "chart" || e.Name == Cx + "chart");

    /// <summary>The object an anchor places: its child that is not a position or client data.</summary>
    internal static XElement? ObjectOf(XElement anchor) =>
        anchor.Elements().FirstOrDefault(e => !(e.Name.Namespace == Xdr && e.Name.LocalName is "from" or "to" or "pos" or "ext" or "clientData"));

    internal static DrawingPosition Position(XElement marker)
    {
        int column = Math.Clamp(XlsxNames.Int((string?)marker.Element(Xdr + "col"), 0), 0, CellAddress.MaxColumns - 1);
        int row = Math.Clamp(XlsxNames.Int((string?)marker.Element(Xdr + "row"), 0), 0, CellAddress.MaxRows - 1);
        return new DrawingPosition(new CellAddress(row, column), Long(marker.Element(Xdr + "colOff")), Long(marker.Element(Xdr + "rowOff")));
    }

    internal static long Long(XObject? node)
    {
        string? text = node switch
        {
            XAttribute attribute => attribute.Value,
            XElement element => element.Value,
            _ => null,
        };
        return long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long value) ? value : 0;
    }

    /// <summary>The cells whose notes the VML drawing shows all the time; empty when the drawing cannot be read as XML.</summary>
    internal static HashSet<CellAddress> VisibleNotes(OpcPart? vml)
    {
        HashSet<CellAddress> visible = [];
        if (vml is null)
        {
            return visible;
        }

        // Excel's VML is not always well-formed XML; the notes then read as hidden.
        if (SafeXml.TryLoad(vml) is not { } document)
        {
            return visible;
        }

        XNamespace x = "urn:schemas-microsoft-com:office:excel";
        foreach (XElement data in document.Descendants(x + "ClientData"))
        {
            if ((string?)data.Attribute("ObjectType") == "Note" && data.Element(x + "Visible") is not null
                && int.TryParse((string?)data.Element(x + "Row"), out int row) && int.TryParse((string?)data.Element(x + "Column"), out int column)
                && row is >= 0 and < CellAddress.MaxRows && column is >= 0 and < CellAddress.MaxColumns)
            {
                visible.Add(new CellAddress(row, column));
            }
        }

        return visible;
    }
}
