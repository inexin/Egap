// Generated from one table of properties (tools/gen-formats.py); edit the table, not this file.
namespace Egap.Documents;

/// <summary>Table cell formatting: width, merges, vertical alignment, margins, borders and shading. Every property is nullable: null means not set here.</summary>
public sealed partial class CellFormat : FormatBase
{
    internal const int Count = 19;

    internal const int WidthId = 0;
    internal const int GridSpanId = 1;
    internal const int VerticalMergeId = 2;
    internal const int VerticalAlignmentId = 3;
    internal const int MarginTopId = 4;
    internal const int MarginBottomId = 5;
    internal const int MarginLeftId = 6;
    internal const int MarginRightId = 7;
    internal const int BorderTopId = 8;
    internal const int BorderBottomId = 9;
    internal const int BorderLeftId = 10;
    internal const int BorderRightId = 11;
    internal const int BorderDiagonalDownId = 12;
    internal const int BorderDiagonalUpId = 13;
    internal const int ShadingId = 14;
    internal const int TextDirectionId = 15;
    internal const int NoWrapId = 16;
    internal const int FitTextId = 17;
    internal const int HideMarkId = 18;

    internal override int PropertyCount => Count;

    /// <summary>The preferred width.</summary>
    public TableWidth? Width
    {
        get => Get<TableWidth>(0);
        set => SetRaw(0, value);
    }

    /// <summary>How many grid columns the cell spans (horizontal merge).</summary>
    public int? GridSpan
    {
        get => Get<int>(1);
        set => SetRaw(1, value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>The cell's part in a vertical merge.</summary>
    public VerticalMerge? VerticalMerge
    {
        get => Get<VerticalMerge>(2);
        set => SetEnum(2, value);
    }

    /// <summary>Where content sits vertically.</summary>
    public VerticalAlignment? VerticalAlignment
    {
        get => Get<VerticalAlignment>(3);
        set => SetEnum(3, value);
    }

    /// <summary>The top margin inside the cell in points.</summary>
    public double? MarginTop
    {
        get => Get<double>(4);
        set => SetLength(4, value);
    }

    /// <summary>The bottom margin inside the cell in points.</summary>
    public double? MarginBottom
    {
        get => Get<double>(5);
        set => SetLength(5, value);
    }

    /// <summary>The start margin inside the cell in points.</summary>
    public double? MarginLeft
    {
        get => Get<double>(6);
        set => SetLength(6, value);
    }

    /// <summary>The end margin inside the cell in points.</summary>
    public double? MarginRight
    {
        get => Get<double>(7);
        set => SetLength(7, value);
    }

    /// <summary>The border along the top.</summary>
    public Border? BorderTop
    {
        get => GetRef<Border>(8);
        set => SetRaw(8, value);
    }

    /// <summary>The border along the bottom.</summary>
    public Border? BorderBottom
    {
        get => GetRef<Border>(9);
        set => SetRaw(9, value);
    }

    /// <summary>The border at the start.</summary>
    public Border? BorderLeft
    {
        get => GetRef<Border>(10);
        set => SetRaw(10, value);
    }

    /// <summary>The border at the end.</summary>
    public Border? BorderRight
    {
        get => GetRef<Border>(11);
        set => SetRaw(11, value);
    }

    /// <summary>A diagonal from the top start corner to the bottom end corner.</summary>
    public Border? BorderDiagonalDown
    {
        get => GetRef<Border>(12);
        set => SetRaw(12, value);
    }

    /// <summary>A diagonal from the bottom start corner to the top end corner.</summary>
    public Border? BorderDiagonalUp
    {
        get => GetRef<Border>(13);
        set => SetRaw(13, value);
    }

    /// <summary>The shading behind the cell.</summary>
    public Shading? Shading
    {
        get => GetRef<Shading>(14);
        set => SetRaw(14, value);
    }

    /// <summary>Which way text runs.</summary>
    public TextDirection? TextDirection
    {
        get => Get<TextDirection>(15);
        set => SetEnum(15, value);
    }

    /// <summary>Text is not wrapped.</summary>
    public bool? NoWrap
    {
        get => Get<bool>(16);
        set => SetRaw(16, value);
    }

    /// <summary>Text is squeezed to fit the width.</summary>
    public bool? FitText
    {
        get => Get<bool>(17);
        set => SetRaw(17, value);
    }

    /// <summary>The cell's end mark takes no height when the cell is empty.</summary>
    public bool? HideMark
    {
        get => Get<bool>(18);
        set => SetRaw(18, value);
    }

    internal override string PropertyName(int id)
    {
        return id switch
        {
            0 => nameof(Width),
            1 => nameof(GridSpan),
            2 => nameof(VerticalMerge),
            3 => nameof(VerticalAlignment),
            4 => nameof(MarginTop),
            5 => nameof(MarginBottom),
            6 => nameof(MarginLeft),
            7 => nameof(MarginRight),
            8 => nameof(BorderTop),
            9 => nameof(BorderBottom),
            10 => nameof(BorderLeft),
            11 => nameof(BorderRight),
            12 => nameof(BorderDiagonalDown),
            13 => nameof(BorderDiagonalUp),
            14 => nameof(Shading),
            15 => nameof(TextDirection),
            16 => nameof(NoWrap),
            17 => nameof(FitText),
            18 => nameof(HideMark),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }
}
