namespace Egap.Documents;

/// <summary>
/// Ruby: a short annotation, such as furigana or pinyin, set in small type over (or under) the inlines it holds, its base
/// (Word's phonetic guide, <c>w:ruby</c>; HTML's <c>&lt;ruby&gt;</c>; ODF's <c>text:ruby</c>).
/// </summary>
/// <remarks>
/// The annotation is not part of the paragraph's text: <see cref="Node.GetText"/>, Find and comparison see the base.
/// Layout sets the annotation as Word does, never hanging over the characters beside its base.
/// </remarks>
public sealed class Ruby : CompositeInline
{
    private string text;
    private RubyPosition position;
    private RubyAlignment alignment;
    private double? fontSize;
    private double? raise;
    private double? baseFontSize;
    private string? language;

    /// <summary>A ruby of <paramref name="document"/> annotating its inlines with <paramref name="text"/>, not yet in a paragraph.</summary>
    public Ruby(Document document, string text = "")
        : base(document)
    {
        ArgumentNullException.ThrowIfNull(text);
        this.text = text;
        TextFormat = Own(new RunFormat());
    }

    /// <summary>The annotation's text.</summary>
    public string Text
    {
        get => text;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            SetProperty(ref text, value);
        }
    }

    /// <summary>The annotation's formatting over its base's, such as its font; its size is <see cref="FontSize"/>.</summary>
    public RunFormat TextFormat { get; }

    /// <summary>Which side of the base the annotation goes on.</summary>
    public RubyPosition Position
    {
        get => position;
        set => SetProperty(ref position, value);
    }

    /// <summary>How the annotation and its base spread over each other.</summary>
    public RubyAlignment Alignment
    {
        get => alignment;
        set => SetProperty(ref alignment, value);
    }

    /// <summary>The annotation's font size in points (Word's <c>w:hps</c>), or null for half the base's.</summary>
    public double? FontSize
    {
        get => fontSize;
        set => SetProperty(ref fontSize, Positive(value));
    }

    /// <summary>
    /// How far the annotation's baseline is above the base's, in points (Word's <c>w:hpsRaise</c>), or null for just clear of
    /// the base.
    /// </summary>
    public double? Raise
    {
        get => raise;
        set => SetProperty(ref raise, value is double v && !double.IsFinite(v) ? throw new ArgumentOutOfRangeException(nameof(value)) : value);
    }

    /// <summary>The base's font size Word set the ruby up for, in points (Word's <c>w:hpsBaseText</c>), or null.</summary>
    public double? BaseFontSize
    {
        get => baseFontSize;
        set => SetProperty(ref baseFontSize, Positive(value));
    }

    /// <summary>The annotation's language, such as ja-JP (Word's <c>w:lid</c>), or null.</summary>
    public string? Language
    {
        get => language;
        set => SetProperty(ref language, value);
    }

    /// <inheritdoc/>
    public override Ruby Clone(bool deep = true)
    {
        return (Ruby)base.Clone(deep);
    }

    /// <summary>The base's text and the annotation, for debugging.</summary>
    public override string ToString()
    {
        return string.Concat(Runs.Select(r => r.Text)) + "(" + text + ")";
    }

    private static double? Positive(double? value)
    {
        return value is double v && !(double.IsFinite(v) && v > 0) ? throw new ArgumentOutOfRangeException(nameof(value), v, "A font size is more than 0.") : value;
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitRubyStart(this);
    }

    private protected override VisitorAction VisitEnd(DocumentVisitor visitor)
    {
        return visitor.VisitRubyEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        Ruby copy = CopyInline(new Ruby(context.Target, text)
        {
            position = position,
            alignment = alignment,
            fontSize = fontSize,
            raise = raise,
            baseFontSize = baseFontSize,
            language = language,
        });
        copy.TextFormat.CopyRaw(TextFormat);
        return copy;
    }
}
