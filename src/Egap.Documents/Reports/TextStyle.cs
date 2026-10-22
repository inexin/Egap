namespace Egap.Documents.Reports;

/// <summary>
/// How text in a report looks: font, size, weight, slant, lines, colours, letter spacing and line height. Immutable: each
/// method returns a changed copy, so a style can be kept and shared (<c>TextStyle.Default.FontSize(18).Bold()</c>).
/// What a style leaves unset comes from the style of the text around it.
/// </summary>
public sealed record TextStyle
{
    /// <summary>A style that sets nothing.</summary>
    public static TextStyle Default { get; } = new();

    /// <summary>The font family, or null to keep the surrounding one.</summary>
    public string? Family { get; private init; }

    /// <summary>The size in points, or null to keep the surrounding one.</summary>
    public double? Size { get; private init; }

    /// <summary>Bold, or null to keep the surrounding weight.</summary>
    public bool? IsBold { get; private init; }

    /// <summary>Italic, or null to keep the surrounding slant.</summary>
    public bool? IsItalic { get; private init; }

    /// <summary>Underlined, or null to keep the surrounding setting.</summary>
    public bool? IsUnderlined { get; private init; }

    /// <summary>Struck through, or null to keep the surrounding setting.</summary>
    public bool? IsStruckThrough { get; private init; }

    /// <summary>The text colour, or null to keep the surrounding one.</summary>
    public DocumentColor? Color { get; private init; }

    /// <summary>The colour behind the text, or null for none.</summary>
    public DocumentColor? Background { get; private init; }

    /// <summary>Extra space between letters in points, or null to keep the surrounding spacing.</summary>
    public double? LetterSpacing { get; private init; }

    /// <summary>The line height as a multiple of the font's, or null to keep the surrounding one.</summary>
    public double? LineHeight { get; private init; }

    /// <summary>A copy in <paramref name="family"/>.</summary>
    public TextStyle FontFamily(string family) => this with { Family = family };

    /// <summary>A copy at <paramref name="size"/> points.</summary>
    public TextStyle FontSize(double size) => this with { Size = size > 0 && double.IsFinite(size) ? size : throw new ArgumentOutOfRangeException(nameof(size)) };

    /// <summary>A copy that is bold, or not.</summary>
    public TextStyle Bold(bool bold = true) => this with { IsBold = bold };

    /// <summary>A copy that is italic, or not.</summary>
    public TextStyle Italic(bool italic = true) => this with { IsItalic = italic };

    /// <summary>A copy that is underlined, or not.</summary>
    public TextStyle Underline(bool underline = true) => this with { IsUnderlined = underline };

    /// <summary>A copy that is struck through, or not.</summary>
    public TextStyle Strikethrough(bool strike = true) => this with { IsStruckThrough = strike };

    /// <summary>A copy in <paramref name="color"/>.</summary>
    public TextStyle FontColor(DocumentColor color) => this with { Color = color };

    /// <summary>A copy in the colour of a hex string such as <c>"336699"</c>.</summary>
    public TextStyle FontColor(string hex) => this with { Color = DocumentColor.FromHex(hex) };

    /// <summary>A copy with <paramref name="color"/> behind the text.</summary>
    public TextStyle BackgroundColor(DocumentColor color) => this with { Background = color };

    /// <summary>A copy with <paramref name="points"/> of extra space between letters.</summary>
    public TextStyle Spacing(double points) => this with { LetterSpacing = points };

    /// <summary>A copy whose lines are <paramref name="multiple"/> times the font's height.</summary>
    public TextStyle LineHeightMultiple(double multiple) => this with { LineHeight = multiple > 0 && double.IsFinite(multiple) ? multiple : throw new ArgumentOutOfRangeException(nameof(multiple)) };

    /// <summary>This style over <paramref name="under"/>: what this one sets wins, the rest comes from it.</summary>
    public TextStyle Over(TextStyle under)
    {
        ArgumentNullException.ThrowIfNull(under);
        return new TextStyle
        {
            Family = Family ?? under.Family,
            Size = Size ?? under.Size,
            IsBold = IsBold ?? under.IsBold,
            IsItalic = IsItalic ?? under.IsItalic,
            IsUnderlined = IsUnderlined ?? under.IsUnderlined,
            IsStruckThrough = IsStruckThrough ?? under.IsStruckThrough,
            Color = Color ?? under.Color,
            Background = Background ?? under.Background,
            LetterSpacing = LetterSpacing ?? under.LetterSpacing,
            LineHeight = LineHeight ?? under.LineHeight,
        };
    }

    /// <summary>Sets on <paramref name="format"/> what this style sets beyond <paramref name="inherited"/>.</summary>
    internal void ApplyTo(RunFormat format, TextStyle inherited)
    {
        TextStyle style = Over(inherited);
        if (style.Family is { } family)
        {
            format.FontAscii = family;
            format.FontHighAnsi = family;
            format.FontComplexScript = family;
        }

        if (style.Size is double size)
        {
            format.Size = size;
        }

        if (style.IsBold is bool bold)
        {
            format.Bold = bold;
        }

        if (style.IsItalic is bool italic)
        {
            format.Italic = italic;
        }

        if (style.IsUnderlined is bool underline)
        {
            format.Underline = underline ? UnderlineStyle.Single : UnderlineStyle.None;
        }

        if (style.IsStruckThrough is bool strike)
        {
            format.Strikethrough = strike;
        }

        if (style.Color is { } color)
        {
            format.Color = color;
        }

        if (style.Background is { } background)
        {
            format.Shading = Shading.Solid(background);
        }

        if (style.LetterSpacing is double spacing)
        {
            format.Spacing = spacing;
        }
    }

    /// <summary>Sets a paragraph's line height from this style.</summary>
    internal void ApplyTo(ParagraphFormat format)
    {
        if (LineHeight is double multiple)
        {
            format.LineSpacingRule = LineSpacingRule.Multiple;
            format.LineSpacing = multiple;
        }
    }
}
