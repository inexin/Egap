// Generated from one table of properties (tools/gen-formats.py); edit the table, not this file.
namespace Egap.Documents;

/// <summary>Character formatting: font, size, colour, bold and the rest (Word's run properties). Every property is nullable: null means not set here.</summary>
public sealed partial class RunFormat : FormatBase
{
    internal const int Count = 44;

    /// <summary>The toggle properties, whose values from styles combine as Word's do (ECMA-376 17.7.3).</summary>
    internal static readonly int[] Toggles = [0, 1, 2, 3, 6, 7, 8, 9, 10, 11, 12, 13, 14];

    internal const int BoldId = 0;
    internal const int ItalicId = 1;
    internal const int BoldComplexScriptId = 2;
    internal const int ItalicComplexScriptId = 3;
    internal const int UnderlineId = 4;
    internal const int UnderlineColorId = 5;
    internal const int StrikethroughId = 6;
    internal const int DoubleStrikethroughId = 7;
    internal const int AllCapsId = 8;
    internal const int SmallCapsId = 9;
    internal const int HiddenId = 10;
    internal const int EmbossId = 11;
    internal const int ImprintId = 12;
    internal const int OutlineId = 13;
    internal const int ShadowId = 14;
    internal const int SizeId = 15;
    internal const int SizeComplexScriptId = 16;
    internal const int ColorId = 17;
    internal const int HighlightId = 18;
    internal const int ShadingId = 19;
    internal const int BorderId = 20;
    internal const int VerticalPositionId = 21;
    internal const int PositionId = 22;
    internal const int SpacingId = 23;
    internal const int ScaleId = 24;
    internal const int KerningId = 25;
    internal const int FontAsciiId = 26;
    internal const int FontHighAnsiId = 27;
    internal const int FontEastAsiaId = 28;
    internal const int FontComplexScriptId = 29;
    internal const int ThemeFontAsciiId = 30;
    internal const int ThemeFontHighAnsiId = 31;
    internal const int ThemeFontEastAsiaId = 32;
    internal const int ThemeFontComplexScriptId = 33;
    internal const int LanguageId = 34;
    internal const int LanguageEastAsiaId = 35;
    internal const int LanguageBidiId = 36;
    internal const int RightToLeftId = 37;
    internal const int ComplexScriptId = 38;
    internal const int NoProofId = 39;
    internal const int SpecialHiddenId = 40;
    internal const int WordSpacingId = 41;
    internal const int OverlineId = 42;
    internal const int FeaturesId = 43;

    internal override int PropertyCount => Count;

    /// <summary>Bold text (Latin and East Asian scripts).</summary>
    public bool? Bold
    {
        get => Get<bool>(0);
        set => SetRaw(0, value);
    }

    /// <summary>Italic text (Latin and East Asian scripts).</summary>
    public bool? Italic
    {
        get => Get<bool>(1);
        set => SetRaw(1, value);
    }

    /// <summary>Bold for complex scripts such as Arabic and Hebrew.</summary>
    public bool? BoldComplexScript
    {
        get => Get<bool>(2);
        set => SetRaw(2, value);
    }

    /// <summary>Italic for complex scripts.</summary>
    public bool? ItalicComplexScript
    {
        get => Get<bool>(3);
        set => SetRaw(3, value);
    }

    /// <summary>The underline.</summary>
    public UnderlineStyle? Underline
    {
        get => Get<UnderlineStyle>(4);
        set => SetEnum(4, value);
    }

    /// <summary>The underline's colour; automatic follows the text colour.</summary>
    public DocumentColor? UnderlineColor
    {
        get => Get<DocumentColor>(5);
        set => SetRaw(5, value);
    }

    /// <summary>A line through the text.</summary>
    public bool? Strikethrough
    {
        get => Get<bool>(6);
        set => SetRaw(6, value);
    }

    /// <summary>Two lines through the text.</summary>
    public bool? DoubleStrikethrough
    {
        get => Get<bool>(7);
        set => SetRaw(7, value);
    }

    /// <summary>Shown in capitals, stored as typed.</summary>
    public bool? AllCaps
    {
        get => Get<bool>(8);
        set => SetRaw(8, value);
    }

    /// <summary>Lower-case letters shown as small capitals.</summary>
    public bool? SmallCaps
    {
        get => Get<bool>(9);
        set => SetRaw(9, value);
    }

    /// <summary>Hidden text: kept, not shown or laid out.</summary>
    public bool? Hidden
    {
        get => Get<bool>(10);
        set => SetRaw(10, value);
    }

    /// <summary>Embossed.</summary>
    public bool? Emboss
    {
        get => Get<bool>(11);
        set => SetRaw(11, value);
    }

    /// <summary>Engraved.</summary>
    public bool? Imprint
    {
        get => Get<bool>(12);
        set => SetRaw(12, value);
    }

    /// <summary>Drawn as an outline.</summary>
    public bool? Outline
    {
        get => Get<bool>(13);
        set => SetRaw(13, value);
    }

    /// <summary>With a shadow.</summary>
    public bool? Shadow
    {
        get => Get<bool>(14);
        set => SetRaw(14, value);
    }

    /// <summary>The font size in points (Latin and East Asian scripts).</summary>
    public double? Size
    {
        get => Get<double>(15);
        set => SetLength(15, value);
    }

    /// <summary>The font size in points for complex scripts.</summary>
    public double? SizeComplexScript
    {
        get => Get<double>(16);
        set => SetLength(16, value);
    }

    /// <summary>The text colour.</summary>
    public DocumentColor? Color
    {
        get => Get<DocumentColor>(17);
        set => SetRaw(17, value);
    }

    /// <summary>The highlight behind the text.</summary>
    public HighlightColor? Highlight
    {
        get => Get<HighlightColor>(18);
        set => SetEnum(18, value);
    }

    /// <summary>The shading behind the text.</summary>
    public Shading? Shading
    {
        get => GetRef<Shading>(19);
        set => SetRaw(19, value);
    }

    /// <summary>A border around the text.</summary>
    public Border? Border
    {
        get => GetRef<Border>(20);
        set => SetRaw(20, value);
    }

    /// <summary>Superscript or subscript.</summary>
    public VerticalTextPosition? VerticalPosition
    {
        get => Get<VerticalTextPosition>(21);
        set => SetEnum(21, value);
    }

    /// <summary>How far the text is raised (positive) or lowered (negative), in points.</summary>
    public double? Position
    {
        get => Get<double>(22);
        set => SetLength(22, value, allowNegative: true);
    }

    /// <summary>Extra space after each character in points; negative condenses.</summary>
    public double? Spacing
    {
        get => Get<double>(23);
        set => SetLength(23, value, allowNegative: true);
    }

    /// <summary>Horizontal scaling in per cent, 100 for none.</summary>
    public double? Scale
    {
        get => Get<double>(24);
        set => SetLength(24, value);
    }

    /// <summary>The font size in points from which pairs are kerned; 0 for none.</summary>
    public double? Kerning
    {
        get => Get<double>(25);
        set => SetLength(25, value);
    }

    /// <summary>The font for ASCII characters.</summary>
    public string? FontAscii
    {
        get => GetRef<string>(26);
        set => SetRaw(26, value);
    }

    /// <summary>The font for other Latin characters.</summary>
    public string? FontHighAnsi
    {
        get => GetRef<string>(27);
        set => SetRaw(27, value);
    }

    /// <summary>The font for East Asian characters.</summary>
    public string? FontEastAsia
    {
        get => GetRef<string>(28);
        set => SetRaw(28, value);
    }

    /// <summary>The font for complex scripts.</summary>
    public string? FontComplexScript
    {
        get => GetRef<string>(29);
        set => SetRaw(29, value);
    }

    /// <summary>The theme font for ASCII characters; wins over FontAscii set at the same level.</summary>
    public ThemeFont? ThemeFontAscii
    {
        get => Get<ThemeFont>(30);
        set => SetEnum(30, value);
    }

    /// <summary>The theme font for other Latin characters.</summary>
    public ThemeFont? ThemeFontHighAnsi
    {
        get => Get<ThemeFont>(31);
        set => SetEnum(31, value);
    }

    /// <summary>The theme font for East Asian characters.</summary>
    public ThemeFont? ThemeFontEastAsia
    {
        get => Get<ThemeFont>(32);
        set => SetEnum(32, value);
    }

    /// <summary>The theme font for complex scripts.</summary>
    public ThemeFont? ThemeFontComplexScript
    {
        get => Get<ThemeFont>(33);
        set => SetEnum(33, value);
    }

    /// <summary>The language of Latin text, as a BCP 47 tag such as en-US.</summary>
    public string? Language
    {
        get => GetRef<string>(34);
        set => SetRaw(34, value);
    }

    /// <summary>The language of East Asian text.</summary>
    public string? LanguageEastAsia
    {
        get => GetRef<string>(35);
        set => SetRaw(35, value);
    }

    /// <summary>The language of complex script text.</summary>
    public string? LanguageBidi
    {
        get => GetRef<string>(36);
        set => SetRaw(36, value);
    }

    /// <summary>Right-to-left text.</summary>
    public bool? RightToLeft
    {
        get => Get<bool>(37);
        set => SetRaw(37, value);
    }

    /// <summary>Treat the text as complex script whatever its characters.</summary>
    public bool? ComplexScript
    {
        get => Get<bool>(38);
        set => SetRaw(38, value);
    }

    /// <summary>Leave out of spelling and grammar checks.</summary>
    public bool? NoProof
    {
        get => Get<bool>(39);
        set => SetRaw(39, value);
    }

    /// <summary>Word's specVanish: hidden even when hidden text is shown (used by style separators).</summary>
    public bool? SpecialHidden
    {
        get => Get<bool>(40);
        set => SetRaw(40, value);
    }

    /// <summary>Extra space added to each space between words, in points; negative tightens. Word has no such setting.</summary>
    public double? WordSpacing
    {
        get => Get<double>(41);
        set => SetLength(41, value, allowNegative: true);
    }

    /// <summary>A line over the text. Word has no such setting.</summary>
    public bool? Overline
    {
        get => Get<bool>(42);
        set => SetRaw(42, value);
    }

    /// <summary>OpenType features turned on or off, such as ligatures and tabular figures, on top of the defaults.</summary>
    public FontFeatures? Features
    {
        get => GetRef<FontFeatures>(43);
        set => SetRaw(43, value);
    }

    internal override string PropertyName(int id)
    {
        return id switch
        {
            0 => nameof(Bold),
            1 => nameof(Italic),
            2 => nameof(BoldComplexScript),
            3 => nameof(ItalicComplexScript),
            4 => nameof(Underline),
            5 => nameof(UnderlineColor),
            6 => nameof(Strikethrough),
            7 => nameof(DoubleStrikethrough),
            8 => nameof(AllCaps),
            9 => nameof(SmallCaps),
            10 => nameof(Hidden),
            11 => nameof(Emboss),
            12 => nameof(Imprint),
            13 => nameof(Outline),
            14 => nameof(Shadow),
            15 => nameof(Size),
            16 => nameof(SizeComplexScript),
            17 => nameof(Color),
            18 => nameof(Highlight),
            19 => nameof(Shading),
            20 => nameof(Border),
            21 => nameof(VerticalPosition),
            22 => nameof(Position),
            23 => nameof(Spacing),
            24 => nameof(Scale),
            25 => nameof(Kerning),
            26 => nameof(FontAscii),
            27 => nameof(FontHighAnsi),
            28 => nameof(FontEastAsia),
            29 => nameof(FontComplexScript),
            30 => nameof(ThemeFontAscii),
            31 => nameof(ThemeFontHighAnsi),
            32 => nameof(ThemeFontEastAsia),
            33 => nameof(ThemeFontComplexScript),
            34 => nameof(Language),
            35 => nameof(LanguageEastAsia),
            36 => nameof(LanguageBidi),
            37 => nameof(RightToLeft),
            38 => nameof(ComplexScript),
            39 => nameof(NoProof),
            40 => nameof(SpecialHidden),
            41 => nameof(WordSpacing),
            42 => nameof(Overline),
            43 => nameof(Features),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }
}
