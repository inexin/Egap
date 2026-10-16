// Generated from one table of properties (tools/gen-formats.py); edit the table, not this file.
namespace Egap.Documents;

/// <summary>A section's page setup: paper size, orientation, margins, columns, page numbering and where it starts. Every property is nullable: null means not set here.</summary>
public sealed partial class PageSetup : FormatBase
{
    internal const int Count = 22;

    internal const int PageWidthId = 0;
    internal const int PageHeightId = 1;
    internal const int OrientationId = 2;
    internal const int MarginTopId = 3;
    internal const int MarginBottomId = 4;
    internal const int MarginLeftId = 5;
    internal const int MarginRightId = 6;
    internal const int HeaderDistanceId = 7;
    internal const int FooterDistanceId = 8;
    internal const int GutterId = 9;
    internal const int SectionStartId = 10;
    internal const int DifferentFirstPageId = 11;
    internal const int VerticalAlignmentId = 12;
    internal const int PageNumberStartId = 13;
    internal const int PageNumberStyleId = 14;
    internal const int ColumnCountId = 15;
    internal const int ColumnSpacingId = 16;
    internal const int EqualColumnWidthsId = 17;
    internal const int ColumnsId = 18;
    internal const int ColumnSeparatorId = 19;
    internal const int TextDirectionId = 20;
    internal const int BidiId = 21;

    internal override int PropertyCount => Count;

    /// <summary>The page width in points.</summary>
    public double? PageWidth
    {
        get => Get<double>(0);
        set => SetLength(0, value);
    }

    /// <summary>The page height in points.</summary>
    public double? PageHeight
    {
        get => Get<double>(1);
        set => SetLength(1, value);
    }

    /// <summary>Portrait or landscape; the width and height already reflect it.</summary>
    public PageOrientation? Orientation
    {
        get => Get<PageOrientation>(2);
        set => SetEnum(2, value);
    }

    /// <summary>The top margin in points; negative keeps text from moving below a header.</summary>
    public double? MarginTop
    {
        get => Get<double>(3);
        set => SetLength(3, value, allowNegative: true);
    }

    /// <summary>The bottom margin in points.</summary>
    public double? MarginBottom
    {
        get => Get<double>(4);
        set => SetLength(4, value, allowNegative: true);
    }

    /// <summary>The left margin in points.</summary>
    public double? MarginLeft
    {
        get => Get<double>(5);
        set => SetLength(5, value);
    }

    /// <summary>The right margin in points.</summary>
    public double? MarginRight
    {
        get => Get<double>(6);
        set => SetLength(6, value);
    }

    /// <summary>From the top edge to the header, in points.</summary>
    public double? HeaderDistance
    {
        get => Get<double>(7);
        set => SetLength(7, value);
    }

    /// <summary>From the bottom edge to the footer, in points.</summary>
    public double? FooterDistance
    {
        get => Get<double>(8);
        set => SetLength(8, value);
    }

    /// <summary>Extra margin for binding, in points.</summary>
    public double? Gutter
    {
        get => Get<double>(9);
        set => SetLength(9, value);
    }

    /// <summary>Where the section starts.</summary>
    public SectionStart? SectionStart
    {
        get => Get<SectionStart>(10);
        set => SetEnum(10, value);
    }

    /// <summary>The first page has its own header and footer.</summary>
    public bool? DifferentFirstPage
    {
        get => Get<bool>(11);
        set => SetRaw(11, value);
    }

    /// <summary>Where the text sits on the page vertically.</summary>
    public VerticalAlignment? VerticalAlignment
    {
        get => Get<VerticalAlignment>(12);
        set => SetEnum(12, value);
    }

    /// <summary>The first page number, when the section restarts numbering.</summary>
    public int? PageNumberStart
    {
        get => Get<int>(13);
        set => SetRaw(13, value);
    }

    /// <summary>How page numbers are written.</summary>
    public NumberStyle? PageNumberStyle
    {
        get => Get<NumberStyle>(14);
        set => SetEnum(14, value);
    }

    /// <summary>The number of text columns.</summary>
    public int? ColumnCount
    {
        get => Get<int>(15);
        set => SetRaw(15, value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>The space between equal columns in points.</summary>
    public double? ColumnSpacing
    {
        get => Get<double>(16);
        set => SetLength(16, value);
    }

    /// <summary>Whether columns are equal; otherwise Columns gives each one.</summary>
    public bool? EqualColumnWidths
    {
        get => Get<bool>(17);
        set => SetRaw(17, value);
    }

    /// <summary>Each column's width and the space after it, for unequal columns.</summary>
    public IReadOnlyList<TextColumn>? Columns
    {
        get => GetRef<IReadOnlyList<TextColumn>>(18);
        set => SetRaw(18, value is null ? null : (TextColumn[])[.. value]);
    }

    /// <summary>A line between columns.</summary>
    public bool? ColumnSeparator
    {
        get => Get<bool>(19);
        set => SetRaw(19, value);
    }

    /// <summary>Which way text runs.</summary>
    public TextDirection? TextDirection
    {
        get => Get<TextDirection>(20);
        set => SetEnum(20, value);
    }

    /// <summary>A right-to-left section: columns from the right.</summary>
    public bool? Bidi
    {
        get => Get<bool>(21);
        set => SetRaw(21, value);
    }

    internal override string PropertyName(int id)
    {
        return id switch
        {
            0 => nameof(PageWidth),
            1 => nameof(PageHeight),
            2 => nameof(Orientation),
            3 => nameof(MarginTop),
            4 => nameof(MarginBottom),
            5 => nameof(MarginLeft),
            6 => nameof(MarginRight),
            7 => nameof(HeaderDistance),
            8 => nameof(FooterDistance),
            9 => nameof(Gutter),
            10 => nameof(SectionStart),
            11 => nameof(DifferentFirstPage),
            12 => nameof(VerticalAlignment),
            13 => nameof(PageNumberStart),
            14 => nameof(PageNumberStyle),
            15 => nameof(ColumnCount),
            16 => nameof(ColumnSpacing),
            17 => nameof(EqualColumnWidths),
            18 => nameof(Columns),
            19 => nameof(ColumnSeparator),
            20 => nameof(TextDirection),
            21 => nameof(Bidi),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }
}
