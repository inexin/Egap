// Generated from one table of properties (tools/gen-formats.py); edit the table, not this file.
namespace Egap.Documents;

/// <summary>Table formatting: width, alignment, layout, default cell margins, borders, shading and style options. Every property is nullable: null means not set here.</summary>
public sealed partial class TableFormat : FormatBase
{
    internal const int Count = 26;

    internal const int WidthId = 0;
    internal const int AlignmentId = 1;
    internal const int IndentId = 2;
    internal const int LayoutId = 3;
    internal const int CellSpacingId = 4;
    internal const int CellMarginTopId = 5;
    internal const int CellMarginBottomId = 6;
    internal const int CellMarginLeftId = 7;
    internal const int CellMarginRightId = 8;
    internal const int BorderTopId = 9;
    internal const int BorderBottomId = 10;
    internal const int BorderLeftId = 11;
    internal const int BorderRightId = 12;
    internal const int BorderInsideHorizontalId = 13;
    internal const int BorderInsideVerticalId = 14;
    internal const int ShadingId = 15;
    internal const int BidiId = 16;
    internal const int RowBandSizeId = 17;
    internal const int ColumnBandSizeId = 18;
    internal const int LookFirstRowId = 19;
    internal const int LookLastRowId = 20;
    internal const int LookFirstColumnId = 21;
    internal const int LookLastColumnId = 22;
    internal const int LookNoRowBandsId = 23;
    internal const int LookNoColumnBandsId = 24;
    internal const int PositionId = 25;

    internal override int PropertyCount => Count;

    /// <summary>The preferred width.</summary>
    public TableWidth? Width
    {
        get => Get<TableWidth>(0);
        set => SetRaw(0, value);
    }

    /// <summary>Where the table sits between the margins.</summary>
    public TableAlignment? Alignment
    {
        get => Get<TableAlignment>(1);
        set => SetEnum(1, value);
    }

    /// <summary>The indent from the start margin in points, for a left-aligned table.</summary>
    public double? Indent
    {
        get => Get<double>(2);
        set => SetLength(2, value, allowNegative: true);
    }

    /// <summary>Fixed or autofit column widths.</summary>
    public TableLayoutKind? Layout
    {
        get => Get<TableLayoutKind>(3);
        set => SetEnum(3, value);
    }

    /// <summary>Space between cells in points.</summary>
    public double? CellSpacing
    {
        get => Get<double>(4);
        set => SetLength(4, value);
    }

    /// <summary>The default top margin inside cells, in points.</summary>
    public double? CellMarginTop
    {
        get => Get<double>(5);
        set => SetLength(5, value);
    }

    /// <summary>The default bottom margin inside cells, in points.</summary>
    public double? CellMarginBottom
    {
        get => Get<double>(6);
        set => SetLength(6, value);
    }

    /// <summary>The default start margin inside cells, in points.</summary>
    public double? CellMarginLeft
    {
        get => Get<double>(7);
        set => SetLength(7, value);
    }

    /// <summary>The default end margin inside cells, in points.</summary>
    public double? CellMarginRight
    {
        get => Get<double>(8);
        set => SetLength(8, value);
    }

    /// <summary>The border along the top.</summary>
    public Border? BorderTop
    {
        get => GetRef<Border>(9);
        set => SetRaw(9, value);
    }

    /// <summary>The border along the bottom.</summary>
    public Border? BorderBottom
    {
        get => GetRef<Border>(10);
        set => SetRaw(10, value);
    }

    /// <summary>The border at the start.</summary>
    public Border? BorderLeft
    {
        get => GetRef<Border>(11);
        set => SetRaw(11, value);
    }

    /// <summary>The border at the end.</summary>
    public Border? BorderRight
    {
        get => GetRef<Border>(12);
        set => SetRaw(12, value);
    }

    /// <summary>The borders between rows.</summary>
    public Border? BorderInsideHorizontal
    {
        get => GetRef<Border>(13);
        set => SetRaw(13, value);
    }

    /// <summary>The borders between columns.</summary>
    public Border? BorderInsideVertical
    {
        get => GetRef<Border>(14);
        set => SetRaw(14, value);
    }

    /// <summary>The shading behind every cell.</summary>
    public Shading? Shading
    {
        get => GetRef<Shading>(15);
        set => SetRaw(15, value);
    }

    /// <summary>A right-to-left table: the first column on the right.</summary>
    public bool? Bidi
    {
        get => Get<bool>(16);
        set => SetRaw(16, value);
    }

    /// <summary>How many rows each band of a table style's banded rows covers.</summary>
    public int? RowBandSize
    {
        get => Get<int>(17);
        set => SetRaw(17, value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>How many columns each band covers.</summary>
    public int? ColumnBandSize
    {
        get => Get<int>(18);
        set => SetRaw(18, value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>The table style's first row formatting applies.</summary>
    public bool? LookFirstRow
    {
        get => Get<bool>(19);
        set => SetRaw(19, value);
    }

    /// <summary>The table style's last row formatting applies.</summary>
    public bool? LookLastRow
    {
        get => Get<bool>(20);
        set => SetRaw(20, value);
    }

    /// <summary>The table style's first column formatting applies.</summary>
    public bool? LookFirstColumn
    {
        get => Get<bool>(21);
        set => SetRaw(21, value);
    }

    /// <summary>The table style's last column formatting applies.</summary>
    public bool? LookLastColumn
    {
        get => Get<bool>(22);
        set => SetRaw(22, value);
    }

    /// <summary>The table style's banded rows do not apply.</summary>
    public bool? LookNoRowBands
    {
        get => Get<bool>(23);
        set => SetRaw(23, value);
    }

    /// <summary>The table style's banded columns do not apply.</summary>
    public bool? LookNoColumnBands
    {
        get => Get<bool>(24);
        set => SetRaw(24, value);
    }

    /// <summary>Where a floating table sits and how text wraps around it (Word's tblpPr and tblOverlap); null for a table in the text.</summary>
    public TablePosition? Position
    {
        get => GetRef<TablePosition>(25);
        set => SetRaw(25, value);
    }

    internal override string PropertyName(int id)
    {
        return id switch
        {
            0 => nameof(Width),
            1 => nameof(Alignment),
            2 => nameof(Indent),
            3 => nameof(Layout),
            4 => nameof(CellSpacing),
            5 => nameof(CellMarginTop),
            6 => nameof(CellMarginBottom),
            7 => nameof(CellMarginLeft),
            8 => nameof(CellMarginRight),
            9 => nameof(BorderTop),
            10 => nameof(BorderBottom),
            11 => nameof(BorderLeft),
            12 => nameof(BorderRight),
            13 => nameof(BorderInsideHorizontal),
            14 => nameof(BorderInsideVertical),
            15 => nameof(Shading),
            16 => nameof(Bidi),
            17 => nameof(RowBandSize),
            18 => nameof(ColumnBandSize),
            19 => nameof(LookFirstRow),
            20 => nameof(LookLastRow),
            21 => nameof(LookFirstColumn),
            22 => nameof(LookLastColumn),
            23 => nameof(LookNoRowBands),
            24 => nameof(LookNoColumnBands),
            25 => nameof(Position),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }
}
