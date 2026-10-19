using Egami.Pixels;

namespace Egap.Documents;

/// <summary>
/// A run's effective formatting (<see cref="Run.GetEffectiveFormat"/>): every property with its final value, styles and
/// defaults applied, theme fonts named. Where neither the document nor Word gives a value, Word's own defaults apply (10 pt,
/// Times New Roman, automatic colour).
/// </summary>
public sealed class ResolvedRunFormat
{
    private readonly object?[] values;
    private readonly Theme theme;

    internal ResolvedRunFormat(object?[] values, Theme theme)
    {
        this.values = values;
        this.theme = theme;
    }

    /// <summary>Bold (Latin and East Asian scripts).</summary>
    public bool Bold => Flag(RunFormat.BoldId);

    /// <summary>Italic (Latin and East Asian scripts).</summary>
    public bool Italic => Flag(RunFormat.ItalicId);

    /// <summary>Bold for complex scripts.</summary>
    public bool BoldComplexScript => Flag(RunFormat.BoldComplexScriptId);

    /// <summary>Italic for complex scripts.</summary>
    public bool ItalicComplexScript => Flag(RunFormat.ItalicComplexScriptId);

    /// <summary>The underline.</summary>
    public UnderlineStyle Underline => Value(RunFormat.UnderlineId, UnderlineStyle.None);

    /// <summary>The underline's colour.</summary>
    public DocumentColor UnderlineColor => Value(RunFormat.UnderlineColorId, DocumentColor.Auto);

    /// <summary>A line through the text.</summary>
    public bool Strikethrough => Flag(RunFormat.StrikethroughId);

    /// <summary>Two lines through the text.</summary>
    public bool DoubleStrikethrough => Flag(RunFormat.DoubleStrikethroughId);

    /// <summary>Shown in capitals.</summary>
    public bool AllCaps => Flag(RunFormat.AllCapsId);

    /// <summary>Small capitals.</summary>
    public bool SmallCaps => Flag(RunFormat.SmallCapsId);

    /// <summary>Hidden text.</summary>
    public bool Hidden => Flag(RunFormat.HiddenId);

    /// <summary>Embossed.</summary>
    public bool Emboss => Flag(RunFormat.EmbossId);

    /// <summary>Engraved.</summary>
    public bool Imprint => Flag(RunFormat.ImprintId);

    /// <summary>Drawn as an outline.</summary>
    public bool Outline => Flag(RunFormat.OutlineId);

    /// <summary>With a shadow.</summary>
    public bool Shadow => Flag(RunFormat.ShadowId);

    /// <summary>The font size in points.</summary>
    public double Size => Value(RunFormat.SizeId, 10.0);

    /// <summary>The font size in points for complex scripts.</summary>
    public double SizeComplexScript => Value(RunFormat.SizeComplexScriptId, 10.0);

    /// <summary>The text colour as given.</summary>
    public DocumentColor Color => Value(RunFormat.ColorId, DocumentColor.Auto);

    /// <summary>The colour to draw the text in: theme colours looked up, automatic as black.</summary>
    public Rgba8 TextColor => Color.Resolve(theme, new Rgba8(0, 0, 0, 255));

    /// <summary>The highlight.</summary>
    public HighlightColor Highlight => Value(RunFormat.HighlightId, HighlightColor.None);

    /// <summary>The shading behind the text, or null.</summary>
    public Shading? Shading => values[RunFormat.ShadingId] as Shading;

    /// <summary>A border around the text, or null.</summary>
    public Border? Border => values[RunFormat.BorderId] as Border;

    /// <summary>Superscript or subscript.</summary>
    public VerticalTextPosition VerticalPosition => Value(RunFormat.VerticalPositionId, VerticalTextPosition.Baseline);

    /// <summary>How far the text is raised in points (negative lowers).</summary>
    public double Position => Value(RunFormat.PositionId, 0.0);

    /// <summary>Extra space after each character in points.</summary>
    public double Spacing => Value(RunFormat.SpacingId, 0.0);

    /// <summary>Horizontal scaling in per cent.</summary>
    public double Scale => Value(RunFormat.ScaleId, 100.0);

    /// <summary>The size in points from which pairs are kerned; 0 for none.</summary>
    public double Kerning => Value(RunFormat.KerningId, 0.0);

    /// <summary>The font for ASCII characters, theme fonts looked up.</summary>
    public string FontAscii => Font(0);

    /// <summary>The font for other Latin characters.</summary>
    public string FontHighAnsi => Font(1);

    /// <summary>The font for East Asian characters.</summary>
    public string FontEastAsia => Font(2);

    /// <summary>The font for complex scripts.</summary>
    public string FontComplexScript => Font(3);

    /// <summary>The language of Latin text, or null.</summary>
    public string? Language => values[RunFormat.LanguageId] as string;

    /// <summary>The language of East Asian text, or null.</summary>
    public string? LanguageEastAsia => values[RunFormat.LanguageEastAsiaId] as string;

    /// <summary>The language of complex script text, or null.</summary>
    public string? LanguageBidi => values[RunFormat.LanguageBidiId] as string;

    /// <summary>Right-to-left text.</summary>
    public bool RightToLeft => Flag(RunFormat.RightToLeftId);

    /// <summary>Treated as complex script.</summary>
    public bool ComplexScript => Flag(RunFormat.ComplexScriptId);

    /// <summary>Left out of proofing.</summary>
    public bool NoProof => Flag(RunFormat.NoProofId);

    /// <summary>Hidden even when hidden text is shown.</summary>
    public bool SpecialHidden => Flag(RunFormat.SpecialHiddenId);

    /// <summary>Extra space added to each space between words, in points.</summary>
    public double WordSpacing => Value(RunFormat.WordSpacingId, 0.0);

    /// <summary>A line over the text.</summary>
    public bool Overline => Flag(RunFormat.OverlineId);

    /// <summary>OpenType features turned on or off on top of the defaults; empty for none.</summary>
    public FontFeatures Features => values[RunFormat.FeaturesId] as FontFeatures ?? FontFeatures.None;

    /// <summary>The effective values as a stand-alone <see cref="RunFormat"/>, every property the chain set filled in.</summary>
    public RunFormat ToRunFormat()
    {
        RunFormat format = new();
        for (int i = 0; i < values.Length; i++)
        {
            format.SetRaw(i, values[i]);
        }

        return format;
    }

    private bool Flag(int id)
    {
        return values[id] is true;
    }

    private T Value<T>(int id, T fallback)
        where T : struct
    {
        return values[id] is T value ? value : fallback;
    }

    private string Font(int slot)
    {
        if (values[RunFormat.ThemeFontAsciiId + slot] is ThemeFont font && theme.GetFont(font) is { } themed)
        {
            return themed;
        }

        return values[RunFormat.FontAsciiId + slot] as string ?? (slot == 0 ? "Times New Roman" : Font(0));
    }
}

/// <summary>
/// A paragraph's effective formatting (<see cref="Paragraph.GetEffectiveFormat"/>): every property with its final value, styles
/// and defaults applied, tab stops added up. Where neither the document nor Word gives a value, Word's defaults apply.
/// </summary>
public sealed class ResolvedParagraphFormat
{
    private readonly object?[] values;

    internal ResolvedParagraphFormat(object?[] values)
    {
        this.values = values;
    }

    /// <summary>How lines line up.</summary>
    public ParagraphAlignment Alignment => Value(ParagraphFormat.AlignmentId, ParagraphAlignment.Left);

    /// <summary>The start indent in points.</summary>
    public double LeftIndent => Value(ParagraphFormat.LeftIndentId, 0.0);

    /// <summary>The end indent in points.</summary>
    public double RightIndent => Value(ParagraphFormat.RightIndentId, 0.0);

    /// <summary>The first line's extra indent in points (negative hangs).</summary>
    public double FirstLineIndent => Value(ParagraphFormat.FirstLineIndentId, 0.0);

    /// <summary>Space before in points.</summary>
    public double SpaceBefore => Value(ParagraphFormat.SpaceBeforeId, 0.0);

    /// <summary>Space after in points.</summary>
    public double SpaceAfter => Value(ParagraphFormat.SpaceAfterId, 0.0);

    /// <summary>Whether the space before is chosen by the application.</summary>
    public bool SpaceBeforeAuto => Flag(ParagraphFormat.SpaceBeforeAutoId);

    /// <summary>Whether the space after is chosen by the application.</summary>
    public bool SpaceAfterAuto => Flag(ParagraphFormat.SpaceAfterAutoId);

    /// <summary>The line spacing, read as <see cref="LineSpacingRule"/> says.</summary>
    public double LineSpacing => Value(ParagraphFormat.LineSpacingId, 1.0);

    /// <summary>How <see cref="LineSpacing"/> is read.</summary>
    public LineSpacingRule LineSpacingRule => Value(ParagraphFormat.LineSpacingRuleId, LineSpacingRule.Multiple);

    /// <summary>Kept on the page of the next paragraph.</summary>
    public bool KeepWithNext => Flag(ParagraphFormat.KeepWithNextId);

    /// <summary>All lines on one page.</summary>
    public bool KeepTogether => Flag(ParagraphFormat.KeepTogetherId);

    /// <summary>Starts on a new page.</summary>
    public bool PageBreakBefore => Flag(ParagraphFormat.PageBreakBeforeId);

    /// <summary>Widow and orphan control; on unless a level turns it off, as in Word.</summary>
    public bool WidowControl => values[ParagraphFormat.WidowControlId] as bool? ?? true;

    /// <summary>The outline level, 0 to 8, or 9 for body text.</summary>
    public int OutlineLevel => Value(ParagraphFormat.OutlineLevelId, 9);

    /// <summary>The tab stops in position order, Clear stops applied.</summary>
    public IReadOnlyList<TabStop> TabStops => (TabStop[])values[ParagraphFormat.TabStopsId]!;

    /// <summary>The border above, or null.</summary>
    public Border? BorderTop => values[ParagraphFormat.BorderTopId] as Border;

    /// <summary>The border below, or null.</summary>
    public Border? BorderBottom => values[ParagraphFormat.BorderBottomId] as Border;

    /// <summary>The border at the start, or null.</summary>
    public Border? BorderLeft => values[ParagraphFormat.BorderLeftId] as Border;

    /// <summary>The border at the end, or null.</summary>
    public Border? BorderRight => values[ParagraphFormat.BorderRightId] as Border;

    /// <summary>The border between paragraphs with the same borders, or null.</summary>
    public Border? BorderBetween => values[ParagraphFormat.BorderBetweenId] as Border;

    /// <summary>The bar border, or null.</summary>
    public Border? BorderBar => values[ParagraphFormat.BorderBarId] as Border;

    /// <summary>The shading, or null.</summary>
    public Shading? Shading => values[ParagraphFormat.ShadingId] as Shading;

    /// <summary>No space between paragraphs of the same style.</summary>
    public bool ContextualSpacing => Flag(ParagraphFormat.ContextualSpacingId);

    /// <summary>Right-to-left paragraph.</summary>
    public bool Bidi => Flag(ParagraphFormat.BidiId);

    /// <summary>Left out of line numbering.</summary>
    public bool SuppressLineNumbers => Flag(ParagraphFormat.SuppressLineNumbersId);

    /// <summary>Not hyphenated automatically.</summary>
    public bool SuppressAutoHyphens => Flag(ParagraphFormat.SuppressAutoHyphensId);

    /// <summary>Lines follow the document grid; on unless turned off, as in Word.</summary>
    public bool SnapToGrid => values[ParagraphFormat.SnapToGridId] as bool? ?? true;

    /// <summary>Indents swap on even pages.</summary>
    public bool MirrorIndents => Flag(ParagraphFormat.MirrorIndentsId);

    /// <summary>The list the paragraph is in, or null (also when a level says <see cref="Documents.List.None"/>).</summary>
    public List? List => values[ParagraphFormat.ListId] is List list && list != Documents.List.None && list.Document is { } d && d.Lists.Contains(list) ? list : null;

    /// <summary>The paragraph's level in its list, 0 to 8.</summary>
    public int ListLevel => Value(ParagraphFormat.ListLevelId, 0);

    /// <summary>Where lines may break inside words.</summary>
    public WordBreak WordBreak => Value(ParagraphFormat.WordBreakId, WordBreak.Normal);

    /// <summary>The most lines shown, 0 for no limit.</summary>
    public int MaxLines => Value(ParagraphFormat.MaxLinesId, 0);

    /// <summary>The effective values as a stand-alone <see cref="ParagraphFormat"/>.</summary>
    public ParagraphFormat ToParagraphFormat()
    {
        ParagraphFormat format = new();
        for (int i = 0; i < values.Length; i++)
        {
            format.SetRaw(i, values[i]);
        }

        return format;
    }

    private bool Flag(int id)
    {
        return values[id] is true;
    }

    private T Value<T>(int id, T fallback)
        where T : struct
    {
        return values[id] is T value ? value : fallback;
    }
}
