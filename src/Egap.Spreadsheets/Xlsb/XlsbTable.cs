namespace Egap.Spreadsheets.Xlsb;

/// <summary>A table part of an XLSB file ([MS-XLSB] 2.1.7.56): the table's range, name, columns and style.</summary>
internal sealed class XlsbTable
{
    /// <summary>A column: its name and what its totals row shows.</summary>
    public sealed record Column(string Name, TableTotalsFunction Function, string? Label);

    /// <summary>The table's id, which structured references name.</summary>
    public uint Id { get; private init; }

    public string DisplayName { get; private init; } = "";

    public RangeAddress Range { get; private init; }

    public int HeaderRows { get; private init; }

    public int TotalsRows { get; private init; }

    public List<Column> Columns { get; } = [];

    public bool HasFilter { get; private set; }

    public bool HasStyleInfo { get; private set; }

    public string? StyleName { get; private set; }

    public bool RowStripes { get; private set; }

    public bool ColumnStripes { get; private set; }

    public bool FirstColumn { get; private set; }

    public bool LastColumn { get; private set; }

    /// <summary>The table of a part; null for a part with no BrtBeginList or a range off the sheet.</summary>
    public static XlsbTable? Read(ReadOnlySpan<byte> data)
    {
        XlsbTable? table = null;
        XlsbRecords records = new(data);
        while (records.Next())
        {
            XlsbReader record = records.Reader;
            switch (records.Type)
            {
                case 343 when table is null: // BrtBeginList
                    {
                        (int r1, int r2, int c1, int c2) = record.Range();
                        record.UInt32();
                        uint id = record.UInt32();
                        int header = record.Int32();
                        int totals = record.Int32();
                        record.Skip(4 + (6 * 4) + 4);
                        string name = record.NullableWideString() ?? "";
                        string display = record.NullableWideString() ?? name;
                        if (r1 < 0 || c1 < 0 || r1 > r2 || c1 > c2 || r2 >= CellAddress.MaxRows || c2 >= CellAddress.MaxColumns)
                        {
                            return null;
                        }

                        table = new XlsbTable
                        {
                            Id = id,
                            DisplayName = display,
                            Range = new RangeAddress(r1, c1, r2, c2),
                            HeaderRows = header,
                            TotalsRows = totals,
                        };
                        break;
                    }

                case 161 when table is not null: // BrtBeginAFilter
                    table.HasFilter = true;
                    break;
                case 347 when table is not null: // BrtBeginListCol
                    {
                        record.UInt32();
                        uint function = record.UInt32();
                        record.Skip(4 * 4);
                        record.NullableWideString();
                        string caption = record.NullableWideString() ?? "";
                        string? label = record.Remaining >= 4 ? record.NullableWideString() : null;
                        table.Columns.Add(new Column(caption, Function(function), string.IsNullOrEmpty(label) ? null : label));
                        break;
                    }

                case 513 when table is not null: // BrtTableStyleClient
                    {
                        int flags = record.UInt16();
                        table.HasStyleInfo = true;
                        table.StyleName = record.Remaining >= 4 ? record.NullableWideString() : null;
                        table.FirstColumn = (flags & 0x01) != 0;
                        table.LastColumn = (flags & 0x02) != 0;
                        table.RowStripes = (flags & 0x04) != 0;
                        table.ColumnStripes = (flags & 0x08) != 0;
                        break;
                    }
            }
        }

        return table;
    }

    private static TableTotalsFunction Function(uint value) => value switch
    {
        1 => TableTotalsFunction.Average,
        2 => TableTotalsFunction.Count,
        3 => TableTotalsFunction.CountNumbers,
        4 => TableTotalsFunction.Max,
        5 => TableTotalsFunction.Min,
        6 => TableTotalsFunction.Sum,
        7 => TableTotalsFunction.StandardDeviation,
        8 => TableTotalsFunction.Variance,
        9 => TableTotalsFunction.Custom,
        _ => TableTotalsFunction.None,
    };
}
