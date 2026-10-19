// Generated from one table of properties (tools/gen-formats.py); edit the table, not this file.
namespace Egap.Documents;

/// <summary>Table row formatting: height, header repeat and page splitting. Every property is nullable: null means not set here.</summary>
public sealed partial class RowFormat : FormatBase
{
    internal const int Count = 8;

    internal const int HeightId = 0;
    internal const int HeightRuleId = 1;
    internal const int IsHeaderId = 2;
    internal const int IsFooterId = 3;
    internal const int CantSplitId = 4;
    internal const int AlignmentId = 5;
    internal const int CellSpacingId = 6;
    internal const int HiddenId = 7;

    internal override int PropertyCount => Count;

    /// <summary>The height in points, read as HeightRule says.</summary>
    public double? Height
    {
        get => Get<double>(0);
        set => SetLength(0, value);
    }

    /// <summary>How Height is read.</summary>
    public RowHeightRule? HeightRule
    {
        get => Get<RowHeightRule>(1);
        set => SetEnum(1, value);
    }

    /// <summary>Repeated at the top of each page the table continues on.</summary>
    public bool? IsHeader
    {
        get => Get<bool>(2);
        set => SetRaw(2, value);
    }

    /// <summary>Among the table's last rows, repeated at the bottom of each page the table continues from. Word has no such setting.</summary>
    public bool? IsFooter
    {
        get => Get<bool>(3);
        set => SetRaw(3, value);
    }

    /// <summary>Kept on one page.</summary>
    public bool? CantSplit
    {
        get => Get<bool>(4);
        set => SetRaw(4, value);
    }

    /// <summary>Where the row sits, overriding the table's alignment.</summary>
    public TableAlignment? Alignment
    {
        get => Get<TableAlignment>(5);
        set => SetEnum(5, value);
    }

    /// <summary>Space between the row's cells in points.</summary>
    public double? CellSpacing
    {
        get => Get<double>(6);
        set => SetLength(6, value);
    }

    /// <summary>Hidden row.</summary>
    public bool? Hidden
    {
        get => Get<bool>(7);
        set => SetRaw(7, value);
    }

    internal override string PropertyName(int id)
    {
        return id switch
        {
            0 => nameof(Height),
            1 => nameof(HeightRule),
            2 => nameof(IsHeader),
            3 => nameof(IsFooter),
            4 => nameof(CantSplit),
            5 => nameof(Alignment),
            6 => nameof(CellSpacing),
            7 => nameof(Hidden),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }
}
