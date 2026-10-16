// Generated from one table of properties (tools/gen-formats.py); edit the table, not this file.
namespace Egap.Documents;

/// <summary>Table row formatting: height, header repeat and page splitting. Every property is nullable: null means not set here.</summary>
public sealed partial class RowFormat : FormatBase
{
    internal const int Count = 7;

    internal const int HeightId = 0;
    internal const int HeightRuleId = 1;
    internal const int IsHeaderId = 2;
    internal const int CantSplitId = 3;
    internal const int AlignmentId = 4;
    internal const int CellSpacingId = 5;
    internal const int HiddenId = 6;

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

    /// <summary>Kept on one page.</summary>
    public bool? CantSplit
    {
        get => Get<bool>(3);
        set => SetRaw(3, value);
    }

    /// <summary>Where the row sits, overriding the table's alignment.</summary>
    public TableAlignment? Alignment
    {
        get => Get<TableAlignment>(4);
        set => SetEnum(4, value);
    }

    /// <summary>Space between the row's cells in points.</summary>
    public double? CellSpacing
    {
        get => Get<double>(5);
        set => SetLength(5, value);
    }

    /// <summary>Hidden row.</summary>
    public bool? Hidden
    {
        get => Get<bool>(6);
        set => SetRaw(6, value);
    }

    internal override string PropertyName(int id)
    {
        return id switch
        {
            0 => nameof(Height),
            1 => nameof(HeightRule),
            2 => nameof(IsHeader),
            3 => nameof(CantSplit),
            4 => nameof(Alignment),
            5 => nameof(CellSpacing),
            6 => nameof(Hidden),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }
}
