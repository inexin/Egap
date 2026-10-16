namespace Egap.Documents;

/// <summary>
/// A node that goes into a paragraph: a run of text, a tab or a break. Fields, hyperlinks, bookmarks, notes, comments and
/// drawings join in Build Plan task 5.
/// </summary>
public abstract class Inline : Node
{
    private Revision? revision;

    private protected Inline(Document document)
        : base(document)
    {
        ArgumentNullException.ThrowIfNull(document);
    }

    /// <summary>The paragraph this inline is in, directly or through a hyperlink or content control, or null.</summary>
    public Paragraph? Paragraph => GetAncestor<Paragraph>();

    /// <summary>A tracked insertion, deletion or move of this inline, or null.</summary>
    public Revision? Revision
    {
        get => revision;
        set => SetProperty(ref revision, value);
    }

    /// <summary>Copies the settings every inline has, for clones.</summary>
    private protected T CopyInline<T>(T copy)
        where T : Inline
    {
        copy.revision = revision;
        return copy;
    }
}

/// <summary>A run of text with one formatting (Build Plan task 3).</summary>
public sealed class Run : Inline
{
    private string text;
    private CharacterStyle? style;
    private FormatRevision? formatRevision;

    /// <summary>A run of <paramref name="document"/> holding <paramref name="text"/>, not yet in a paragraph.</summary>
    public Run(Document document, string text = "")
        : base(document)
    {
        ArgumentNullException.ThrowIfNull(text);
        this.text = text;
        Format = Own(new RunFormat());
    }

    /// <summary>The run's direct formatting, over its style's.</summary>
    public RunFormat Format { get; }

    /// <summary>A tracked change of the run's formatting, with the formatting it had before, or null.</summary>
    public FormatRevision? FormatRevision
    {
        get => formatRevision;
        set
        {
            if (value is not null && value.OldFormat is not RunFormat)
            {
                throw new ArgumentException("A run's format revision holds a RunFormat.", nameof(value));
            }

            SetProperty(ref formatRevision, value);
        }
    }

    /// <summary>The run's character style, or null for the document's default character style.</summary>
    public CharacterStyle? Style
    {
        get => style;
        set => SetStyle(ref style, value);
    }

    /// <summary>
    /// The formatting the run is drawn with: document defaults, then the table style, paragraph style, character style and
    /// direct formatting, in that order (Phase 6 design Q5), with theme fonts and colours named.
    /// </summary>
    public ResolvedRunFormat GetEffectiveFormat()
    {
        return FormatResolver.Resolve(this);
    }

    /// <summary>The text; tabs and breaks are their own inlines, though a run may hold any character a file gave it.</summary>
    public string Text
    {
        get => text;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value != text)
            {
                text = value;
                Document.NotifyChanged(this, DocumentChangeKind.TextChanged, this);
            }
        }
    }

    /// <inheritdoc/>
    public override Run Clone(bool deep = true)
    {
        return (Run)base.Clone(deep);
    }

    /// <summary>The text, for debugging.</summary>
    public override string ToString()
    {
        return text;
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitRun(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        Run copy = CopyInline(new Run(context.Target, text) { style = context.Map(style), formatRevision = formatRevision });
        copy.Format.CopyRaw(Format);
        return copy;
    }
}

/// <summary>A tab: the text moves on to the paragraph's next tab stop.</summary>
public sealed class Tab : Inline
{
    /// <summary>A tab of <paramref name="document"/>, not yet in a paragraph.</summary>
    public Tab(Document document)
        : base(document)
    {
    }

    /// <inheritdoc/>
    public override Tab Clone(bool deep = true)
    {
        return (Tab)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitTab(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return CopyInline(new Tab(context.Target));
    }
}

/// <summary>What a <see cref="Break"/> ends.</summary>
public enum BreakKind
{
    /// <summary>The line: the text goes on at the start of the next line in the same paragraph.</summary>
    Line,

    /// <summary>The page: the text goes on at the top of the next page.</summary>
    Page,

    /// <summary>The column: the text goes on at the top of the next column, or the next page in a one-column section.</summary>
    Column,
}

/// <summary>A line, page or column break inside a paragraph.</summary>
public sealed class Break : Inline
{
    private BreakKind kind;

    /// <summary>A break of <paramref name="kind"/> in <paramref name="document"/>, not yet in a paragraph.</summary>
    public Break(Document document, BreakKind kind = BreakKind.Line)
        : base(document)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        this.kind = kind;
    }

    /// <summary>What the break ends.</summary>
    public BreakKind Kind
    {
        get => kind;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (value != kind)
            {
                kind = value;
                Document.NotifyChanged(this, DocumentChangeKind.PropertyChanged, this, property: nameof(Kind));
            }
        }
    }

    /// <inheritdoc/>
    public override Break Clone(bool deep = true)
    {
        return (Break)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitBreak(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return CopyInline(new Break(context.Target, kind));
    }
}
