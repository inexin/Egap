using System.Globalization;
using System.Xml.Linq;

using Egap.Spreadsheets.Pivots;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// GETPIVOTDATA: a value a pivot table shows, found by its data field and the items of its fields. Microsoft's function
/// reference is the source; what counts as a match was measured in Excel 16.0 build 20527.
/// </summary>
internal static partial class FormulaFunctions
{
    private static IEnumerable<FormulaFunction> PivotFunctions() =>
    [
        new("GETPIVOTDATA", 2, 254, [ArgumentKind.Value, ArgumentKind.Any, ArgumentKind.Value], GetPivotData),
    ];

    /// <summary>
    /// GETPIVOTDATA(data_field, pivot_table, [field1, item1], ...): the value in the table's data area on the row and column
    /// whose items are the ones named; fields not named are totalled, so the row or column must be a total the table shows.
    /// The data field is its name or its source field's name; a field is its source name; an item matches its value, as
    /// text without case or as a number, and an empty item is "(blank)". An empty value cell gives 0; anything not shown,
    /// and an error among the fields and items, gives #REF!. The value is the one the table shows, as it was last laid out.
    /// </summary>
    private static EvalValue GetPivotData(EvalValue[] arguments, EvalContext context)
    {
        if (arguments.Length % 2 != 0)
        {
            return Error(CellError.Reference);
        }

        for (int i = 0; i < 2; i++)
        {
            if (arguments[i].IsError)
            {
                return arguments[i];
            }
        }

        if (arguments[1].Kind != EvalKind.Reference)
        {
            return Error(CellError.Reference);
        }

        SheetArea area = arguments[1].Areas[0];
        PivotTable? table = context.Book.Pivots.Tables(area.Sheet).FirstOrDefault(t => t.Range.Contains(area.Range.First));
        if (table?.Cache is not { } cache || arguments[0].Kind != EvalKind.Text)
        {
            return Error(CellError.Reference);
        }

        PivotDefinition definition = table.Definition;
        int data = DataFieldNamed(definition, cache, arguments[0].Text);
        if (data < 0)
        {
            return Error(CellError.Reference);
        }

        // The items asked for, by field.
        Dictionary<int, EvalValue> wanted = [];
        for (int i = 2; i < arguments.Length; i += 2)
        {
            int field = arguments[i].Kind == EvalKind.Text
                ? cache.Fields.FindIndex(f => string.Equals(f.Name, arguments[i].Text, StringComparison.OrdinalIgnoreCase))
                : -1;
            // A field named twice takes the last item.
            if (field < 0 || field >= definition.Fields.Count || arguments[i + 1].IsError)
            {
                return Error(CellError.Reference);
            }

            wanted[field] = arguments[i + 1];
        }

        foreach ((int field, EvalValue item) in wanted)
        {
            bool onAxis = definition.RowFields.Contains(field) || definition.ColumnFields.Contains(field);
            if (!onAxis)
            {
                // A page field matches only the item it shows.
                PageFieldDefinition? page = definition.PageFields.FirstOrDefault(p => p.Field == field);
                if (page?.Item is not int shown || shown >= definition.Fields[field].Items.Count
                    || !ItemMatches(definition.Fields[field].Items[shown], cache.Fields[field], item, context))
                {
                    return Error(CellError.Reference);
                }
            }
        }

        XElement root = definition.Root;
        XNamespace ns = definition.Ns;
        int row = PivotLine(definition, cache, root.Element(ns + "rowItems"), definition.RowFields, wanted, data, context);
        int column = PivotLine(definition, cache, root.Element(ns + "colItems"), definition.ColumnFields, wanted, data, context);
        if (row < 0 || column < 0)
        {
            return Error(CellError.Reference);
        }

        XElement? location = definition.Location;
        int firstDataRow = XlsxNames.Int((string?)location?.Attribute("firstDataRow"), 0);
        int firstDataColumn = XlsxNames.Int((string?)location?.Attribute("firstDataCol"), 0);
        int at = definition.Range.First.Row + firstDataRow + row, across = definition.Range.First.Column + firstDataColumn + column;
        if (at > definition.Range.Last.Row || across > definition.Range.Last.Column)
        {
            return Error(CellError.Reference);
        }

        CellValue value = table.Worksheet[at, across].Value;
        return value.IsEmpty ? Number(0) : EvalValue.FromCell(value);
    }

    /// <summary>The data field a name means: its own name, or the first one whose source field has the name; -1 for none.</summary>
    private static int DataFieldNamed(PivotDefinition definition, PivotCache cache, string name)
    {
        for (int d = 0; d < definition.DataFields.Count; d++)
        {
            if (string.Equals(definition.DataFields[d].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return d;
            }
        }

        for (int d = 0; d < definition.DataFields.Count; d++)
        {
            int field = definition.DataFields[d].Field;
            if ((uint)field < (uint)cache.Fields.Count && string.Equals(cache.Fields[field].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return d;
            }
        }

        return -1;
    }

    /// <summary>
    /// Which line of an axis (row or column items, from 0) shows the items asked for on that axis and totals the axis's other
    /// fields, for the data field; -1 when no line does. An axis with no items is a single line.
    /// </summary>
    private static int PivotLine(PivotDefinition definition, PivotCache cache, XElement? items, List<int> fields, Dictionary<int, EvalValue> wanted, int data, EvalContext context)
    {
        // The fields asked for on this axis must come first, in the axis's order, with the data field anywhere.
        List<int> named = [.. fields.Where(wanted.ContainsKey)];
        List<int> order = [.. fields.Where(f => f != PivotDefinition.DataField)];
        if (!named.SequenceEqual(order.Take(named.Count)))
        {
            return -1;
        }

        if (items is null)
        {
            return named.Count == 0 ? 0 : -1;
        }

        XNamespace ns = items.Name.Namespace;
        List<int> members = [];
        int line = 0;
        foreach (XElement item in items.Elements(ns + "i"))
        {
            string type = (string?)item.Attribute("t") ?? "data";
            int repeat = XlsxNames.Int((string?)item.Attribute("r"), 0);
            int dataIndex = XlsxNames.Int((string?)item.Attribute("i"), 0);
            if (members.Count > repeat)
            {
                members.RemoveRange(repeat, members.Count - repeat);
            }

            foreach (XElement x in item.Elements(ns + "x"))
            {
                members.Add(XlsxNames.Int((string?)x.Attribute("v"), 0));
            }

            if (Matches(type, members, dataIndex))
            {
                return line;
            }

            line++;
        }

        return -1;

        bool Matches(string type, List<int> members, int dataIndex)
        {
            bool dataOnAxis = fields.Contains(PivotDefinition.DataField);
            if (dataOnAxis && dataIndex != data)
            {
                return false;
            }

            if (type == "grand")
            {
                return named.Count == 0;
            }

            // The fields this line names, and whether it shows values for them: a full line does, a subtotal line does, and
            // an outer item's own line does when its field has a subtotal shown at the top.
            int count = 0;
            int last = -1;
            for (int m = 0; m < members.Count && m < fields.Count; m++)
            {
                if (fields[m] == PivotDefinition.DataField)
                {
                    continue;
                }

                last = fields[m];
                count++;
            }

            bool shows = type switch
            {
                "data" => count == order.Count
                    || (count > 0 && definition.Fields[last].Subtotals.Contains("default") && definition.Fields[last].SubtotalTop),
                "default" => true,
                _ => false,
            };
            if (!shows || count != named.Count)
            {
                return false;
            }

            for (int m = 0; m < members.Count && m < fields.Count; m++)
            {
                int field = fields[m];
                if (field == PivotDefinition.DataField)
                {
                    continue;
                }

                List<PivotItemDefinition> fieldItems = definition.Fields[field].Items;
                if ((uint)members[m] >= (uint)fieldItems.Count || !ItemMatches(fieldItems[members[m]], cache.Fields[field], wanted[field], context))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Whether a pivot item is the item asked for: by its caption or value as text without case, or by its number.</summary>
    private static bool ItemMatches(PivotItemDefinition item, PivotCacheField field, in EvalValue wanted, EvalContext context)
    {
        if ((uint)item.Shared >= (uint)field.Items.Count)
        {
            return false;
        }

        PivotValue value = field.Items[item.Shared];
        if (wanted.Kind == EvalKind.Number)
        {
            return value.IsNumeric && value.Number == wanted.Number;
        }

        if (wanted.Kind == EvalKind.Boolean)
        {
            return value.Kind == PivotValueKind.Boolean && (value.Number != 0) == wanted.Boolean;
        }

        if (wanted.Kind != EvalKind.Text || wanted.Text.Length == 0)
        {
            return false;
        }

        string text = item.Caption ?? value.Kind switch
        {
            PivotValueKind.Number => ExcelValues.TryText(EvalValue.FromNumber(value.Number), context.Culture, out string number, out _) ? number : "",
            PivotValueKind.Date => "",
            _ => value.ToString(),
        };
        return string.Equals(text, wanted.Text, StringComparison.OrdinalIgnoreCase)
            || (value.Kind == PivotValueKind.Number && double.TryParse(wanted.Text, NumberStyles.Float, context.Culture, out double parsed) && parsed == value.Number);
    }
}
