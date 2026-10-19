// Generated from one table of properties (tools/gen-formats.py); edit the table, not this file.
namespace Egap.Documents;

/// <summary>Paragraph formatting: alignment, indents, spacing, keep rules, tabs, borders and shading (Word's paragraph properties). Every property is nullable: null means not set here.</summary>
public sealed partial class ParagraphFormat : FormatBase
{
    internal const int Count = 33;

    internal const int AlignmentId = 0;
    internal const int LeftIndentId = 1;
    internal const int RightIndentId = 2;
    internal const int FirstLineIndentId = 3;
    internal const int SpaceBeforeId = 4;
    internal const int SpaceAfterId = 5;
    internal const int SpaceBeforeAutoId = 6;
    internal const int SpaceAfterAutoId = 7;
    internal const int LineSpacingId = 8;
    internal const int LineSpacingRuleId = 9;
    internal const int KeepWithNextId = 10;
    internal const int KeepTogetherId = 11;
    internal const int PageBreakBeforeId = 12;
    internal const int WidowControlId = 13;
    internal const int OutlineLevelId = 14;
    internal const int TabStopsId = 15;
    internal const int BorderTopId = 16;
    internal const int BorderBottomId = 17;
    internal const int BorderLeftId = 18;
    internal const int BorderRightId = 19;
    internal const int BorderBetweenId = 20;
    internal const int BorderBarId = 21;
    internal const int ShadingId = 22;
    internal const int ContextualSpacingId = 23;
    internal const int BidiId = 24;
    internal const int SuppressLineNumbersId = 25;
    internal const int SuppressAutoHyphensId = 26;
    internal const int SnapToGridId = 27;
    internal const int MirrorIndentsId = 28;
    internal const int ListId = 29;
    internal const int ListLevelId = 30;
    internal const int WordBreakId = 31;
    internal const int MaxLinesId = 32;

    internal override int PropertyCount => Count;

    /// <summary>How lines line up between the indents.</summary>
    public ParagraphAlignment? Alignment
    {
        get => Get<ParagraphAlignment>(0);
        set => SetEnum(0, value);
    }

    /// <summary>The indent from the start margin in points.</summary>
    public double? LeftIndent
    {
        get => Get<double>(1);
        set => SetLength(1, value, allowNegative: true);
    }

    /// <summary>The indent from the end margin in points.</summary>
    public double? RightIndent
    {
        get => Get<double>(2);
        set => SetLength(2, value, allowNegative: true);
    }

    /// <summary>The first line's extra indent in points; negative for a hanging indent.</summary>
    public double? FirstLineIndent
    {
        get => Get<double>(3);
        set => SetLength(3, value, allowNegative: true);
    }

    /// <summary>Space before the paragraph in points.</summary>
    public double? SpaceBefore
    {
        get => Get<double>(4);
        set => SetLength(4, value);
    }

    /// <summary>Space after the paragraph in points.</summary>
    public double? SpaceAfter
    {
        get => Get<double>(5);
        set => SetLength(5, value);
    }

    /// <summary>Space before chosen by the application, as HTML's margins (Word's beforeAutospacing).</summary>
    public bool? SpaceBeforeAuto
    {
        get => Get<bool>(6);
        set => SetRaw(6, value);
    }

    /// <summary>Space after chosen by the application.</summary>
    public bool? SpaceAfterAuto
    {
        get => Get<bool>(7);
        set => SetRaw(7, value);
    }

    /// <summary>The line spacing: a multiple of single, or points, as LineSpacingRule says.</summary>
    public double? LineSpacing
    {
        get => Get<double>(8);
        set => SetLength(8, value);
    }

    /// <summary>How LineSpacing is read.</summary>
    public LineSpacingRule? LineSpacingRule
    {
        get => Get<LineSpacingRule>(9);
        set => SetEnum(9, value);
    }

    /// <summary>On the same page as the next paragraph.</summary>
    public bool? KeepWithNext
    {
        get => Get<bool>(10);
        set => SetRaw(10, value);
    }

    /// <summary>All lines on one page.</summary>
    public bool? KeepTogether
    {
        get => Get<bool>(11);
        set => SetRaw(11, value);
    }

    /// <summary>Starts on a new page.</summary>
    public bool? PageBreakBefore
    {
        get => Get<bool>(12);
        set => SetRaw(12, value);
    }

    /// <summary>No single first or last line on its own page.</summary>
    public bool? WidowControl
    {
        get => Get<bool>(13);
        set => SetRaw(13, value);
    }

    /// <summary>The outline level, 0 to 8, or 9 for body text.</summary>
    public int? OutlineLevel
    {
        get => Get<int>(14);
        set => SetRaw(14, value is null or (>= 0 and <= 9) ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>Tab stops added here; a Clear stop removes one a style gives.</summary>
    public IReadOnlyList<TabStop>? TabStops
    {
        get => GetRef<IReadOnlyList<TabStop>>(15);
        set => SetRaw(15, value is null ? null : (TabStop[])[.. value]);
    }

    /// <summary>The border above.</summary>
    public Border? BorderTop
    {
        get => GetRef<Border>(16);
        set => SetRaw(16, value);
    }

    /// <summary>The border below.</summary>
    public Border? BorderBottom
    {
        get => GetRef<Border>(17);
        set => SetRaw(17, value);
    }

    /// <summary>The border at the start.</summary>
    public Border? BorderLeft
    {
        get => GetRef<Border>(18);
        set => SetRaw(18, value);
    }

    /// <summary>The border at the end.</summary>
    public Border? BorderRight
    {
        get => GetRef<Border>(19);
        set => SetRaw(19, value);
    }

    /// <summary>The border between paragraphs with the same borders.</summary>
    public Border? BorderBetween
    {
        get => GetRef<Border>(20);
        set => SetRaw(20, value);
    }

    /// <summary>A bar beside the paragraph, outside its borders.</summary>
    public Border? BorderBar
    {
        get => GetRef<Border>(21);
        set => SetRaw(21, value);
    }

    /// <summary>The shading behind the paragraph.</summary>
    public Shading? Shading
    {
        get => GetRef<Shading>(22);
        set => SetRaw(22, value);
    }

    /// <summary>No space between paragraphs of the same style.</summary>
    public bool? ContextualSpacing
    {
        get => Get<bool>(23);
        set => SetRaw(23, value);
    }

    /// <summary>A right-to-left paragraph.</summary>
    public bool? Bidi
    {
        get => Get<bool>(24);
        set => SetRaw(24, value);
    }

    /// <summary>Left out of line numbering.</summary>
    public bool? SuppressLineNumbers
    {
        get => Get<bool>(25);
        set => SetRaw(25, value);
    }

    /// <summary>Not hyphenated automatically.</summary>
    public bool? SuppressAutoHyphens
    {
        get => Get<bool>(26);
        set => SetRaw(26, value);
    }

    /// <summary>Lines follow the document grid.</summary>
    public bool? SnapToGrid
    {
        get => Get<bool>(27);
        set => SetRaw(27, value);
    }

    /// <summary>Left and right indents swap on even pages.</summary>
    public bool? MirrorIndents
    {
        get => Get<bool>(28);
        set => SetRaw(28, value);
    }

    /// <summary>The list the paragraph is in; List.None takes it out of a list its style gives.</summary>
    public List? List
    {
        get => GetRef<List>(29);
        set => SetList(29, value);
    }

    /// <summary>The paragraph's level in its list, 0 to 8.</summary>
    public int? ListLevel
    {
        get => Get<int>(30);
        set => SetRaw(30, value is null or (>= 0 and <= 8) ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>Where lines may break inside words.</summary>
    public WordBreak? WordBreak
    {
        get => Get<WordBreak>(31);
        set => SetEnum(31, value);
    }

    /// <summary>The most lines shown; the last ends in an ellipsis where text is cut. Word has no such setting.</summary>
    public int? MaxLines
    {
        get => Get<int>(32);
        set => SetRaw(32, value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    internal override string PropertyName(int id)
    {
        return id switch
        {
            0 => nameof(Alignment),
            1 => nameof(LeftIndent),
            2 => nameof(RightIndent),
            3 => nameof(FirstLineIndent),
            4 => nameof(SpaceBefore),
            5 => nameof(SpaceAfter),
            6 => nameof(SpaceBeforeAuto),
            7 => nameof(SpaceAfterAuto),
            8 => nameof(LineSpacing),
            9 => nameof(LineSpacingRule),
            10 => nameof(KeepWithNext),
            11 => nameof(KeepTogether),
            12 => nameof(PageBreakBefore),
            13 => nameof(WidowControl),
            14 => nameof(OutlineLevel),
            15 => nameof(TabStops),
            16 => nameof(BorderTop),
            17 => nameof(BorderBottom),
            18 => nameof(BorderLeft),
            19 => nameof(BorderRight),
            20 => nameof(BorderBetween),
            21 => nameof(BorderBar),
            22 => nameof(Shading),
            23 => nameof(ContextualSpacing),
            24 => nameof(Bidi),
            25 => nameof(SuppressLineNumbers),
            26 => nameof(SuppressAutoHyphens),
            27 => nameof(SnapToGrid),
            28 => nameof(MirrorIndents),
            29 => nameof(List),
            30 => nameof(ListLevel),
            31 => nameof(WordBreak),
            32 => nameof(MaxLines),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }
}
